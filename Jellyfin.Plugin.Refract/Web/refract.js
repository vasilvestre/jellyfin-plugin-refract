const pluginUniqueId = 'c9f4a1d2-7b3e-4f6a-9d21-3e5c8b0a7f14';

function normalize(value) {
    return (value || '').replace(/-/g, '').toLowerCase();
}

// Jellyfin serializes the answer in PascalCase. Read either casing so the page never
// runs on undefined, which once turned the polling delay into NaN.
function pick(source, name) {
    if (!source) {
        return undefined;
    }

    return source[name] !== undefined
        ? source[name]
        : source[name.charAt(0).toUpperCase() + name.slice(1)];
}

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

export default function (view) {
    function currentUserId() {
        return view.querySelector('#selectUser').value;
    }

    function showLinked(linked) {
        view.querySelector('#linkedBlock').classList.toggle('hide', !linked);
        view.querySelector('#unlinkedBlock').classList.toggle('hide', linked);
    }

    function loadUsers() {
        ApiClient.getUsers().then(function (users) {
            const select = view.querySelector('#selectUser');
            select.innerHTML = '';
            users.forEach(function (user) {
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                select.appendChild(option);
            });
            refreshStatus();
        });
    }

    function refreshStatus() {
        const userId = normalize(currentUserId());
        ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
            const linked = (config.RefractUsers || []).some(function (entry) {
                return normalize(entry.LinkedMbUserId) === userId
                    && (entry.RefreshToken || '').length > 0;
            });
            showLinked(linked);
        });
    }

    function poll(userId, seconds) {
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

                ApiClient.getJSON(ApiClient.getUrl('Refract/Users/' + userId + '/PollAuthorizationStatus'))
                    .then(function (result) {
                        const status = pick(result, 'status');
                        if (status === 'authorized') {
                            stopPolling();
                            view.querySelector('#pollStatus').textContent = 'Approved.';
                            view.querySelector('#codeBlock').classList.add('hide');
                            refreshStatus();
                            return;
                        }

                        if (status === 'denied') {
                            stopPolling();
                            view.querySelector('#pollStatus').textContent = 'Refused in the Refract app.';
                            setTimeout(refreshStatus, 1500);
                            return;
                        }

                        if (status === 'expired') {
                            stopPolling();
                            view.querySelector('#pollStatus').textContent = 'The code expired. Start again.';
                            setTimeout(refreshStatus, 1500);
                            return;
                        }

                        if (status === 'slow_down') {
                            delay += 5;
                            view.querySelector('#pollStatus').textContent = 'Waiting, the server asked for slower polling.';
                        } else {
                            view.querySelector('#pollStatus').textContent = 'Waiting for approval.';
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

    view.querySelector('#selectUser').addEventListener('change', function () {
        stopPolling();
        view.querySelector('#codeBlock').classList.add('hide');
        refreshStatus();
    });

    view.querySelector('#authorize').addEventListener('click', function () {
        const userId = currentUserId();
        ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('Refract/Users/' + userId + '/Authorize'),
            dataType: 'json'
        }).then(function (result) {
            const code = pick(result, 'userCode');
            const uri = pick(result, 'verificationUri') || 'https://getrefract.app/device';
            view.querySelector('#userCode').textContent = code || 'open the link below';
            const link = view.querySelector('#verificationLink');
            link.href = uri;
            link.textContent = uri;
            view.querySelector('#pollStatus').textContent = 'Waiting for approval.';
            view.querySelector('#codeBlock').classList.remove('hide');
            poll(userId, pick(result, 'interval'));
        });
    });

    view.querySelector('#deauthorize').addEventListener('click', function () {
        const userId = currentUserId();
        ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('Refract/Users/' + userId + '/Deauthorize'),
            dataType: 'json'
        }).then(function () {
            stopPolling();
            view.querySelector('#codeBlock').classList.add('hide');
            refreshStatus();
        });
    });

    view.addEventListener('viewshow', loadUsers);
    view.addEventListener('viewhide', stopPolling);
}
