const pluginUniqueId = 'c9f4a1d2-7b3e-4f6a-9d21-3e5c8b0a7f14';

// Jellyfin may hand the page a fresh closure while the previous timer still runs.
// One timer for the whole module, plus a generation counter so a stale answer
// never re-arms a poll.
let pollTimer = null;
let pollGeneration = 0;

function stopPolling() {
    pollGeneration += 1;
    if (pollTimer) {
        clearTimeout(pollTimer);
        pollTimer = null;
    }
}

function normalize(value) {
    return (value || '').replace(/-/g, '').toLowerCase();
}

// Jellyfin may serialize the answer in PascalCase. Read either casing so the page
// never runs on undefined, which once turned the polling delay into NaN.
function pick(source, name) {
    if (!source) {
        return undefined;
    }

    return source[name] !== undefined
        ? source[name]
        : source[name.charAt(0).toUpperCase() + name.slice(1)];
}

export default function (view) {
    let isAdmin = false;
    let userId = null;

    function showLinked(linked) {
        view.querySelector('#linkedBlock').classList.toggle('hide', !linked);
        view.querySelector('#unlinkedBlock').classList.toggle('hide', linked);
    }

    function setStatus(text) {
        view.querySelector('#pollStatus').textContent = text;
    }

    function loadAdminUsers() {
        ApiClient.getUsers().then(function (users) {
            const select = view.querySelector('#selectUser');
            select.innerHTML = '';
            users.forEach(function (user) {
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                select.appendChild(option);
            });
            userId = select.value;
            refreshStatus();
        });
    }

    function refreshStatus() {
        if (!userId) {
            return;
        }

        // A non admin cannot read the plugin configuration, so the plugin reports
        // its own linked state and returns one boolean.
        if (!isAdmin) {
            ApiClient.getJSON(ApiClient.getUrl('Refract/Me')).then(function (result) {
                showLinked(!!pick(result, 'linked'));
            });
            return;
        }

        ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
            const linked = (config.RefractUsers || []).some(function (entry) {
                return normalize(entry.LinkedMbUserId) === normalize(userId)
                    && (entry.RefreshToken || '').length > 0;
            });
            showLinked(linked);
        });
    }

    function poll(seconds) {
        const target = userId;
        stopPolling();
        const generation = pollGeneration;
        let delay = Number(seconds);
        if (!Number.isFinite(delay) || delay < 5) {
            delay = 5;
        }

        function tick() {
            if (generation !== pollGeneration) {
                return;
            }

            pollTimer = setTimeout(function () {
                if (generation !== pollGeneration) {
                    return;
                }

                ApiClient.getJSON(ApiClient.getUrl('Refract/Users/' + target + '/PollAuthorizationStatus'))
                    .then(function (result) {
                        const status = pick(result, 'status');
                        if (status === 'authorized') {
                            stopPolling();
                            setStatus('Approved.');
                            view.querySelector('#codeBlock').classList.add('hide');
                            refreshStatus();
                            return;
                        }

                        if (status === 'denied') {
                            stopPolling();
                            setStatus('Refused in the Refract app.');
                            setTimeout(refreshStatus, 1500);
                            return;
                        }

                        if (status === 'expired') {
                            stopPolling();
                            setStatus('The code expired. Start again.');
                            setTimeout(refreshStatus, 1500);
                            return;
                        }

                        if (status === 'slow_down') {
                            delay += 5;
                            setStatus('Waiting, the server asked for slower polling.');
                        } else {
                            setStatus('Waiting for approval.');
                        }

                        tick();
                    })
                    .catch(function () {
                        // Network hiccup or a restarting server: back off instead of spinning.
                        delay += 5;
                        tick();
                    });
            }, delay * 1000);
        }

        tick();
    }

    function startAuthorize() {
        const target = userId;
        ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('Refract/Users/' + target + '/Authorize'),
            dataType: 'json'
        }).then(function (result) {
            const code = pick(result, 'userCode');
            const uri = pick(result, 'verificationUri') || 'https://getrefract.app/device';
            view.querySelector('#userCode').textContent = code || 'open the link below';
            const link = view.querySelector('#verificationLink');
            link.href = uri;
            link.textContent = uri;
            setStatus('Waiting for approval.');
            view.querySelector('#codeBlock').classList.remove('hide');
            poll(pick(result, 'interval'));
        });
    }

    view.querySelector('#selectUser').addEventListener('change', function () {
        stopPolling();
        view.querySelector('#codeBlock').classList.add('hide');
        userId = view.querySelector('#selectUser').value;
        refreshStatus();
    });

    view.querySelector('#authorize').addEventListener('click', startAuthorize);

    view.querySelector('#deauthorize').addEventListener('click', function () {
        const target = userId;
        ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('Refract/Users/' + target + '/Deauthorize'),
            dataType: 'json'
        }).then(function () {
            stopPolling();
            view.querySelector('#codeBlock').classList.add('hide');
            refreshStatus();
        });
    });

    // The button lives in the whole server's web client, not in one user's
    // settings, so only an administrator changes it.
    function loadButtonSetting() {
        ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
            view.querySelector('#showButton').checked = pick(config, 'showLinkButton') !== false;
        });
    }

    view.querySelector('#showButton').addEventListener('change', function (event) {
        ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
            config.ShowLinkButton = event.target.checked;
            ApiClient.updatePluginConfiguration(pluginUniqueId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
    });

    view.addEventListener('viewshow', function () {
        ApiClient.getCurrentUser().then(function (user) {
            isAdmin = !!(user.Policy && user.Policy.IsAdministrator);
            const picker = view.querySelector('#selectUser');
            picker.closest('.selectContainer').classList.toggle('hide', !isAdmin);
            view.querySelector('#showButton').closest('.checkboxContainer').classList.toggle('hide', !isAdmin);
            if (isAdmin) {
                loadButtonSetting();
                loadAdminUsers();
            } else {
                userId = user.Id;
                refreshStatus();
            }
        });
    });

    view.addEventListener('viewhide', stopPolling);
}
