const pluginUniqueId = 'c9f4a1d2-7b3e-4f6a-9d21-3e5c8b0a7f14';

function normalize(value) {
    return (value || '').replace(/-/g, '').toLowerCase();
}

export default function (view) {
    let pollTimer = null;

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

    function stopPolling() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    function poll(statusUrl, interval) {
        stopPolling();
        pollTimer = setInterval(function () {
            ApiClient.getJSON(ApiClient.getUrl(statusUrl)).then(function (result) {
                if (result.status === 'authorized') {
                    stopPolling();
                    view.querySelector('#pollStatus').textContent = 'Approved.';
                    view.querySelector('#codeBlock').classList.add('hide');
                    refreshStatus();
                } else if (result.status === 'pending' || result.status === 'slow_down') {
                    view.querySelector('#pollStatus').textContent =
                        result.status === 'slow_down' ? 'Waiting, the server asked for slower polling.' : 'Waiting for approval.';
                } else {
                    stopPolling();
                    view.querySelector('#pollStatus').textContent =
                        result.status === 'denied' ? 'Refused in the Refract app.' : 'The code expired. Start again.';
                    setTimeout(refreshStatus, 1500);
                }
            });
        }, Math.max(interval, 5) * 1000);
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
            url: ApiClient.getUrl('Refract/Users/' + userId + '/Authorize')
        }).then(function (result) {
            view.querySelector('#userCode').textContent = result.userCode;
            const link = view.querySelector('#verificationLink');
            link.href = result.verificationUri || 'https://getrefract.app/device';
            link.textContent = result.verificationUri || 'getrefract.app/device';
            view.querySelector('#pollStatus').textContent = 'Waiting for approval.';
            view.querySelector('#codeBlock').classList.remove('hide');
            poll('Refract/Users/' + userId + '/PollAuthorizationStatus', result.interval);
        });
    });

    view.querySelector('#deauthorize').addEventListener('click', function () {
        const userId = currentUserId();
        ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('Refract/Users/' + userId + '/Deauthorize')
        }).then(function () {
            stopPolling();
            view.querySelector('#codeBlock').classList.add('hide');
            refreshStatus();
        });
    });

    view.addEventListener('viewshow', loadUsers);
    view.addEventListener('viewhide', stopPolling);
}
