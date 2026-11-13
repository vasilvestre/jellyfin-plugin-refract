// Loaded by a script tag that the plugin injects into index.html. It builds its
// own button and modal so a member links their Refract account without leaving
// the web client and without typing their Jellyfin password again.
(function () {
    var base = location.pathname.replace(/\/web\/.*$/, '');
    var client = 'MediaBrowser Client="Refract", Device="Web", DeviceId="refract-widget", Version="1.0.7.0"';
    var token = null;
    var userId = null;
    var timer = null;
    var captured = null;

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
        return {
            'X-Emby-Token': token,
            'Authorization': client + ', Token="' + token + '"'
        };
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

    async function call(path, init) {
        var response = await fetch(base + path, Object.assign({}, init, {
            headers: Object.assign(auth(), (init && init.headers) || {})
        }));
        if (!response.ok) {
            throw new Error(path + ' answered ' + response.status);
        }

        return response.status === 204 ? null : response.json();
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

    function stop() {
        if (timer) {
            clearTimeout(timer);
            timer = null;
        }
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
        note(target, 'Jellyfin kept no usable session here, so sign in once more.');
        target.insertAdjacentHTML('beforeend',
            '<input id="refract-user" placeholder="Jellyfin username" autocomplete="username" ' +
            'style="width:100%;box-sizing:border-box;margin-bottom:8px;padding:10px;border:1px solid #2c3547;' +
            'border-radius:8px;background:#10131a;color:inherit;font:inherit">' +
            '<input id="refract-pass" type="password" placeholder="Jellyfin password" autocomplete="current-password" ' +
            'style="width:100%;box-sizing:border-box;padding:10px;border:1px solid #2c3547;border-radius:8px;' +
            'background:#10131a;color:inherit;font:inherit">');
        button(target, 'Continue', async function () {
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
                    throw new Error('Jellyfin refused those credentials.');
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
        note(target, 'Checking your Refract account…');
        try {
            var me = await call('/Refract/Me');
            if (me.linked) {
                linked(target);
                return;
            }

            var result = await call('/Refract/Users/' + userId + '/Authorize', { method: 'POST' });
            target.innerHTML = '';
            note(target, 'Type this code in the Refract app, under <em>Access and connected apps</em>, ' +
                'then <em>Approve a device</em>.');
            target.insertAdjacentHTML('beforeend',
                '<div style="margin:12px 0;padding:14px;background:#10131a;border-radius:8px;text-align:center;' +
                'font-size:24px;letter-spacing:4px">' + result.userCode + '</div>' +
                '<div id="refract-qr" style="margin:14px 0;text-align:center"></div>' +
                '<a href="' + result.verificationUri + '" target="_blank" rel="noopener" ' +
                'style="color:#4fc3f7;font-size:13px">Open the approval page</a>' +
                '<p id="refract-state" style="margin:14px 0 0;color:#9aa7bd;font-size:13px">Waiting for approval.</p>');
            renderQr(result.verificationUri);
            await poll(result.interval);
        } catch (error) {
            note(target, error.message, '#ff9f7a');
            button(target, 'Retry', function () { start(target); });
        }
    }

    async function poll(seconds) {
        stop();
        var delay = Math.max(Number(seconds) || 5, 5);
        var tick = async function () {
            try {
                console.debug('refract: polling');
                var status = (await call('/Refract/Users/' + userId + '/PollAuthorizationStatus')).status;
                if (status === 'authorized') {
                    stop();
                    linked(document.getElementById('refract-body'));
                    return;
                }

                if (status === 'denied' || status === 'expired') {
                    stop();
                    var target = document.getElementById('refract-body');
                    target.innerHTML = '';
                    note(target, status === 'denied' ? 'Refused in the Refract app.' : 'The code expired.', '#ff9f7a');
                    button(target, 'Start again', function () { start(target); });
                    return;
                }

                if (status === 'slow_down') {
                    delay += 5;
                }

                var state = document.getElementById('refract-state');
                if (state) {
                    state.textContent = 'Waiting for approval (' + status + ').';
                }
            } catch (error) {
                delay += 5;
                var failed = document.getElementById('refract-state');
                if (failed) {
                    failed.textContent = 'Retrying after an error: ' + error.message;
                }
            }

            timer = setTimeout(tick, delay * 1000);
        };

        tick();
    }

    function linked(target) {
        target.innerHTML = '';
        note(target, 'Your Jellyfin account sends what you watch to your Refract account.', '#7ee0a8');
        button(target, 'Unlink this account', async function () {
            try {
                await call('/Refract/Users/' + userId + '/Deauthorize', { method: 'POST' });
                target.innerHTML = '';
                note(target, 'Unlinked.');
            } catch (error) {
                note(target, error.message, '#ff9f7a');
            }
        });
    }

    async function open() {
        stop();
        var target = sheet();
        target.innerHTML = '';
        note(target, 'Looking for your Jellyfin session…');
        if (await adoptSession()) {
            await start(target);
        } else {
            target.innerHTML = '';
            askCredentials(target);
        }
    }

    // React re-renders the page on navigation, which removes our node, so the
    // button is put back when it disappears.
    function mount() {
        if (!document.body || document.getElementById('refract-link-anchor')) {
            return;
        }

        var link = document.createElement('a');
        link.id = 'refract-link-anchor';
        link.href = '#';
        link.textContent = 'Link Refract';
        link.style.cssText = 'position:fixed;right:16px;bottom:16px;z-index:2147483645;padding:10px 16px;' +
            'border-radius:24px;background:#4fc3f7;color:#10131a;font:600 14px system-ui,sans-serif;' +
            'text-decoration:none;box-shadow:0 6px 20px rgba(0,0,0,.4)';
        link.addEventListener('click', function (event) {
            event.preventDefault();
            open();
        });
        document.body.appendChild(link);
    }

    if (document.body) {
        mount();
    } else {
        document.addEventListener('DOMContentLoaded', mount);
    }

    setInterval(mount, 3000);
}());
