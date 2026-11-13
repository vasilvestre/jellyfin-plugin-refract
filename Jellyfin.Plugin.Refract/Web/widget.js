// Loaded by a script tag that the plugin injects into index.html. It builds its
// own button and modal so a member links their Refract account without leaving
// the web client and without typing their Jellyfin password again.
(function () {
    var base = location.pathname.replace(/\/web\/.*$/, '');
    var client = 'MediaBrowser Client="Refract", Device="Web", DeviceId="refract-widget", Version="1.0.7.0"';
    var token = null;
    var userId = null;
    var pollStop = null;
    var captured = null;
    var isLinked = null;
    var missingCache = null;
    var missingRendering = false;

    // The token stored in localStorage can be an old session the server already
    // forgot. The web client sends the live one on every request, so we copy it
    // from there instead of guessing which stored entry is still valid.
    (function hook() {
        if (window.__refractHook) {
            return;
        }

        window.__refractHook = 1;
        var read = function (value) {
            var match = typeof value === 'string' ? value.match(/Token="([^"]+)"/) : null;
            if (match) {
                captured = match[1];
            }
        };

        var originalFetch = window.fetch;
        window.fetch = function (input, init) {
            try {
                var headers = (init && init.headers) || (input && input.headers);
                if (headers && typeof headers.get === 'function') {
                    read(headers.get('Authorization'));
                } else if (headers) {
                    read(headers.Authorization || headers.authorization);
                }
            } catch (e) {
                // Never let the hook break a client request.
            }

            return originalFetch.apply(this, arguments);
        };

        var setHeader = XMLHttpRequest.prototype.setRequestHeader;
        XMLHttpRequest.prototype.setRequestHeader = function (name, value) {
            try {
                if (name.toLowerCase() === 'authorization') {
                    read(value);
                }
            } catch (e) {
                // Same: the hook stays passive.
            }

            return setHeader.apply(this, arguments);
        };
    }());

    function auth() {
        return window.refractFlow.headers(token, client);
    }

    // Jellyfin keeps one entry per server in this key. An old entry can hold a
    // token the server already forgot, so every token is tried until one answers.
    function storedTokens() {
        try {
            var raw = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
            return (raw.Servers || []).map(function (s) { return s.AccessToken; }).filter(Boolean);
        } catch (e) {
            return [];
        }
    }

    function call(path, init) {
        return window.refractFlow.request(base, client, token, path, init);
    }

    async function adoptSession() {
        for (var wait = 0; wait < 30 && !captured; wait++) {
            await new Promise(function (done) { setTimeout(done, 100); });
        }

        var list = (captured ? [captured] : []).concat(storedTokens());
        for (var i = 0; i < list.length; i++) {
            token = list[i];
            try {
                var session = await call('/Users/Me');
                userId = session.Id;
                return true;
            } catch (e) {
                token = null;
            }
        }

        return false;
    }

    // The button only helps a member who has not linked yet, so the state is read
    // once at startup and again after each link or unlink.
    async function refreshLinked() {
        if (!(await adoptSession())) {
            return;
        }

        try {
            isLinked = !!(await call('/Refract/Me')).linked;
        } catch (e) {
            isLinked = false;
        }

        mount();
    }

    function stop() {
        if (pollStop) {
            pollStop();
            pollStop = null;
        }
    }

    // Jellyfin 12 does not expose its translation dictionary to injected scripts and
    // has no "Link" string, so the labels the widget shows live here.
    var strings = {
        en: {
            link: 'Link Refract',
            checking: 'Checking your Refract account…',
            code: 'Type this code in the Refract app, under <em>Access and connected apps</em>, then <em>Approve a device</em>.',
            open: 'Open the approval page',
            waiting: 'Waiting for approval.',
            waitingFor: 'Waiting for approval (',
            retrying: 'Retrying after an error: ',
            denied: 'Refused in the Refract app.',
            expired: 'The code expired.',
            again: 'Start again',
            retry: 'Retry',
            linked: 'Your Jellyfin account sends what you watch to your Refract account.',
            unlink: 'Unlink this account',
            unlinked: 'Unlinked.',
            looking: 'Looking for your Jellyfin session…',
            session: 'Jellyfin kept no usable session here, so sign in once more.',
            user: 'Jellyfin username',
            pass: 'Jellyfin password',
            cont: 'Continue',
            refused: 'Jellyfin refused those credentials.',
            missing: 'To download via Seerr'
        },
        fr: {
            link: 'Lier Refract',
            checking: 'Vérification de votre compte Refract…',
            code: 'Saisissez ce code dans l\'application Refract, sous <em>Accès et applications connectées</em>, puis <em>Approuver un appareil</em>.',
            open: 'Ouvrir la page d\'approbation',
            waiting: 'En attente d\'approbation.',
            waitingFor: 'En attente d\'approbation (',
            retrying: 'Nouvelle tentative après une erreur : ',
            denied: 'Refusé dans l\'application Refract.',
            expired: 'Le code a expiré.',
            again: 'Recommencer',
            retry: 'Réessayer',
            linked: 'Votre compte Jellyfin envoie ce que vous regardez à votre compte Refract.',
            unlink: 'Délier ce compte',
            unlinked: 'Compte délié.',
            looking: 'Recherche de votre session Jellyfin…',
            session: 'Jellyfin n\'a gardé aucune session utilisable, reconnectez-vous une fois.',
            user: 'Nom d\'utilisateur Jellyfin',
            pass: 'Mot de passe Jellyfin',
            cont: 'Continuer',
            refused: 'Jellyfin a refusé ces identifiants.',
            missing: 'À télécharger via Seerr'
        }
    };
    var lang = (document.documentElement.lang || navigator.language || 'en').slice(0, 2).toLowerCase();

    function t(key) {
        var pack = strings[lang] || strings.en;
        return pack[key] || strings.en[key];
    }

    function sheet() {
        var overlay = document.createElement('div');
        overlay.id = 'refract-overlay';
        overlay.style.cssText = 'position:fixed;inset:0;z-index:2147483646;display:flex;align-items:center;' +
            'justify-content:center;background:rgba(6,9,15,.72);padding:16px';
        overlay.innerHTML =
            '<div style="width:100%;max-width:380px;background:#1b2130;color:#e9eef8;border-radius:14px;' +
            'padding:24px;font:15px/1.5 system-ui,sans-serif;box-shadow:0 20px 60px rgba(0,0,0,.5)">' +
            '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px">' +
            '<strong style="font-size:17px">Refract</strong>' +
            '<button data-close="1" style="background:none;border:0;color:#9aa7bd;font-size:20px;cursor:pointer">&times;</button></div>' +
            '<div id="refract-body"></div></div>';
        overlay.addEventListener('click', function (event) {
            if (event.target === overlay || event.target.getAttribute('data-close')) {
                close();
            }
        });
        document.body.appendChild(overlay);
        return overlay.querySelector('#refract-body');
    }

    function close() {
        stop();
        var overlay = document.getElementById('refract-overlay');
        if (overlay) {
            overlay.remove();
        }

        token = null;
        userId = null;
    }

    function body() {
        return document.getElementById('refract-body') || sheet();
    }

    function note(target, text, colour) {
        target.innerHTML = '<p style="margin:0 0 14px;color:' + (colour || '#9aa7bd') + ';font-size:14px">' + text + '</p>';
        return target;
    }

    function button(target, label, handler) {
        var node = document.createElement('button');
        node.textContent = label;
        node.style.cssText = 'width:100%;margin-top:14px;padding:11px;border:0;border-radius:8px;' +
            'background:#4fc3f7;color:#10131a;font:inherit;font-weight:600;cursor:pointer';
        node.addEventListener('click', handler);
        target.appendChild(node);
        return node;
    }

    function askCredentials(target) {
        note(target, t('session'));
        target.insertAdjacentHTML('beforeend',
            '<input id="refract-user" placeholder="' + t('user') + '" autocomplete="username" ' +
            'style="width:100%;box-sizing:border-box;margin-bottom:8px;padding:10px;border:1px solid #2c3547;' +
            'border-radius:8px;background:#10131a;color:inherit;font:inherit">' +
            '<input id="refract-pass" type="password" placeholder="' + t('pass') + '" autocomplete="current-password" ' +
            'style="width:100%;box-sizing:border-box;padding:10px;border:1px solid #2c3547;border-radius:8px;' +
            'background:#10131a;color:inherit;font:inherit">');
        button(target, t('cont'), async function () {
            try {
                var response = await fetch(base + '/Users/AuthenticateByName', {
                    method: 'POST',
                    headers: { 'Authorization': client, 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        Username: document.getElementById('refract-user').value,
                        Pw: document.getElementById('refract-pass').value
                    })
                });
                if (!response.ok) {
                    throw new Error(t('refused'));
                }

                var session = await response.json();
                token = session.AccessToken;
                userId = session.User.Id;
                await start(target);
            } catch (error) {
                note(target, error.message, '#ff9f7a');
            }
        });
    }

    // Scanning the code saves typing the eight characters on a phone.
    function renderQr(url) {
        var box = document.getElementById('refract-qr');
        if (!box) {
            return;
        }

        var draw = function () {
            try {
                var qr = window.qrcode(0, 'M');
                qr.addData(url);
                qr.make();
                box.innerHTML = qr.createSvgTag({ cellSize: 5, margin: 2 });
                box.firstChild.style.background = '#fff';
            } catch (error) {
                box.textContent = '';
            }
        };

        if (window.qrcode) {
            draw();
            return;
        }

        var script = document.createElement('script');
        script.src = base + '/Refract/Qr';
        script.onload = draw;
        document.head.appendChild(script);
    }

    async function start(target) {
        target.innerHTML = '';
        note(target, t('checking'));
        try {
            var me = await call('/Refract/Me');
            if (me.linked) {
                linked(target);
                return;
            }

            var result = await call('/Refract/Users/' + userId + '/Authorize', { method: 'POST' });
            target.innerHTML = '';
            note(target, t('code'));
            target.insertAdjacentHTML('beforeend',
                '<div style="margin:12px 0;padding:14px;background:#10131a;border-radius:8px;text-align:center;' +
                'font-size:24px;letter-spacing:4px">' + result.userCode + '</div>' +
                '<div id="refract-qr" style="margin:14px 0;text-align:center"></div>' +
                '<a href="' + result.verificationUri + '" target="_blank" rel="noopener" ' +
                'style="color:#4fc3f7;font-size:13px">' + t('open') + '</a>' +
                '<p id="refract-state" style="margin:14px 0 0;color:#9aa7bd;font-size:13px">' + t('waiting') + '</p>');
            renderQr(result.verificationUri);
            await poll(result.interval);
        } catch (error) {
            note(target, error.message, '#ff9f7a');
            button(target, t('retry'), function () { start(target); });
        }
    }

    function poll(seconds) {
        stop();
        pollStop = window.refractFlow.poll(base, client, token, userId, seconds, function (status, detail) {
            if (status === 'authorized') {
                isLinked = true;
                linked(document.getElementById('refract-body'));
                mount();
                return;
            }

            if (status === 'denied' || status === 'expired') {
                var target = document.getElementById('refract-body');
                target.innerHTML = '';
                note(target, status === 'denied' ? t('denied') : t('expired'), '#ff9f7a');
                button(target, t('again'), function () { start(target); });
                return;
            }

            var state = document.getElementById('refract-state');
            if (state) {
                state.textContent = status === 'retry'
                    ? t('retrying') + detail
                    : t('waitingFor') + status + ').';
            }
        });
    }

    function linked(target) {
        target.innerHTML = '';
        note(target, t('linked'), '#7ee0a8');
        button(target, t('unlink'), async function () {
            try {
                await call('/Refract/Users/' + userId + '/Deauthorize', { method: 'POST' });
                isLinked = false;
                target.innerHTML = '';
                note(target, t('unlinked'));
                mount();
            } catch (error) {
                note(target, error.message, '#ff9f7a');
            }
        });
    }

    async function open() {
        stop();
        var target = sheet();
        target.innerHTML = '';
        note(target, t('looking'));
        if (await adoptSession()) {
            await start(target);
        } else {
            target.innerHTML = '';
            askCredentials(target);
        }
    }

    // React re-renders the page on navigation, which removes our node, so the
    // button is put back when it disappears.
    // The button belongs on the browsing pages. Over the player it covers the
    // controls, on the login page there is no account to link yet, the plugin page
    // has its own link controls, and a member who already linked needs no button.
    function placeable() {
        if (!storedTokens().length) {
            return false;
        }

        if (/video|login|configurationpage/i.test(location.hash || '')) {
            return false;
        }

        if (isLinked !== false) {
            return false;
        }

        // A hidden player node can stay in the DOM after the player closes, so
        // only a laid-out one counts.
        var osd = document.querySelector('.videoPlayerContainer, .videoOsd');
        return !osd || osd.getClientRects().length === 0;
    }

    function mount() {
        var existing = document.getElementById('refract-link-anchor');
        if (!document.body || !placeable()) {
            if (existing) {
                existing.remove();
            }

            return;
        }

        if (existing) {
            return;
        }

        var link = document.createElement('a');
        link.id = 'refract-link-anchor';
        link.href = '#';
        link.textContent = t('link');
        link.style.cssText = 'position:fixed;right:16px;bottom:16px;z-index:2147483645;padding:10px 16px;' +
            'border-radius:24px;background:#4fc3f7;color:#10131a;font:600 14px system-ui,sans-serif;' +
            'text-decoration:none;box-shadow:0 6px 20px rgba(0,0,0,.4)';
        link.addEventListener('click', function (event) {
            event.preventDefault();
            open();
        });
        document.body.appendChild(link);
    }

    // KefinTweaks shows the watchlist as a section on a library page or a custom tab.
    // The plugin adds a "to download via Seerr" list there for the Refract watchlist
    // titles that have no local item. Seerr work is reused from Jellyfin Enhanced, so
    // this plugin never holds the Seerr key.
    function watchlistSection() {
        return document.querySelector('.libraryPage:not(.hide) .sections.watchlist, div[id^="customTab_"] .sections.watchlist');
    }

    function watchlistTab() {
        var section = watchlistSection();
        return section ? section.querySelector('div[data-tab="watchlist"]') : null;
    }

    // Jellyfin Enhanced already builds the Seerr result card (poster, media badge,
    // hover synopsis, request button) and exposes it as a reusable builder. The
    // plugin only lists the Refract titles that are not in the library and hands
    // each one to that builder, so the cards look and behave exactly like JE's own
    // search results and this plugin never holds the Seerr key.
    function missingItem(detail, source, tmdbId, mediaType) {
        return {
            id: Number(tmdbId),
            mediaType: mediaType,
            title: detail.title || detail.name || source.title || '',
            name: detail.name || detail.title || source.title || '',
            posterPath: detail.posterPath || detail.poster_path || null,
            voteAverage: detail.voteAverage != null ? detail.voteAverage : (detail.vote_average != null ? detail.vote_average : null),
            releaseDate: detail.releaseDate || detail.release_date || null,
            firstAirDate: detail.firstAirDate || detail.first_air_date || null,
            overview: detail.overview || ''
        };
    }

    function renderMissing(tab, items) {
        var build = window.JellyfinEnhanced
            && window.JellyfinEnhanced.jellyseerrUI
            && window.JellyfinEnhanced.jellyseerrUI.createJellyseerrCard;
        if (!build) {
            return;
        }

        // Same section wrapper, title and scroller KefinTweaks uses for its own
        // watchlist sections, so this list reads as a native section.
        var section = document.createElement('div');
        section.id = 'refract-missing';
        section.className = 'verticalSection emby-scroller-container custom-scroller-container';
        section.setAttribute('data-section-id', 'refract-missing');
        section.setAttribute('data-layout', 'row');

        var titleWrap = document.createElement('div');
        titleWrap.className = 'sectionTitleContainer sectionTitleContainer-cards padded-left';
        var title = document.createElement('h2');
        title.className = 'sectionTitle sectionTitle-cards';
        title.textContent = t('missing');
        titleWrap.appendChild(title);
        section.appendChild(titleWrap);

        var scroller = document.createElement('div');
        scroller.setAttribute('data-horizontal', 'true');
        scroller.setAttribute('data-centerfocus', 'true');
        scroller.className = 'padded-top-focusscale padded-bottom-focusscale emby-scroller padded-left custom-scroller';
        scroller.setAttribute('data-scroll-mode-x', 'custom');

        // Jellyfin's webcomponents shim breaks when given the options argument.
        var row = document.createElement('div');
        row.className = 'focuscontainer-x itemsContainer scrollSlider animatedScrollX';
        row.setAttribute('data-layout', 'row');

        scroller.appendChild(row);
        section.appendChild(scroller);

        items.forEach(function (source) {
            // The poster, synopsis and rating come from Seerr through Jellyfin
            // Enhanced, because Refract does not hold them. The card still renders
            // (without a poster) when that call fails.
            var mediaType = source.mediaType === 'tv' ? 'tv' : 'movie';
            call('/JellyfinEnhanced/jellyseerr/' + mediaType + '/' + source.tmdbId).then(function (detail) {
                row.appendChild(build(missingItem(detail, source, source.tmdbId, mediaType), true, true));
            }).catch(function () {
                row.appendChild(build(missingItem({}, source, source.tmdbId, mediaType), true, true));
            });
        });

        tab.appendChild(section);
    }

    async function mountMissing() {
        if (!token || missingRendering) {
            return;
        }

        var tab = watchlistTab();
        if (!tab || document.getElementById('refract-missing')) {
            return;
        }

        // One in-flight render only. The fetch below can outlast the 3 s tick,
        // so without this guard two ticks would both render the section.
        missingRendering = true;
        try {
            if (missingCache === null) {
                missingCache = (await call('/Refract/WatchlistMissing')).items || [];
            }

            if (!missingCache.length) {
                return;
            }

            renderMissing(tab, missingCache);
        } catch (e) {
            // Jellyfin may still be starting; retry on the next tick.
        } finally {
            missingRendering = false;
        }
    }

    if (document.body) {
        mount();
        refreshLinked();
        mountMissing();
    } else {
        document.addEventListener('DOMContentLoaded', function () {
            mount();
            refreshLinked();
            mountMissing();
        });
    }

    window.addEventListener('hashchange', mount);
    window.addEventListener('hashchange', mountMissing);
    setInterval(mount, 3000);
    setInterval(mountMissing, 3000);
}());
