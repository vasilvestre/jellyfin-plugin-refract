// Shared device-code sign-in flow for the widget and the standalone link page.
// Both load this first and call window.refractFlow for the auth headers, the
// authenticated fetch wrapper and the approval poll, so that logic lives once.
window.refractFlow = (function () {
    function headers(token, client) {
        return { 'X-Emby-Token': token, 'Authorization': client + ', Token="' + token + '"' };
    }

    async function request(base, client, token, path, init) {
        var response = await fetch(base + path, Object.assign({}, init, {
            headers: Object.assign(headers(token, client), (init && init.headers) || {})
        }));
        if (!response.ok) {
            throw new Error(path + ' answered ' + response.status);
        }
        return response.status === 204 ? null : response.json();
    }

    // Polls the device code until the member approves it or it ends. Calls
    // onStatus(status, detail) with 'authorized', 'denied', 'expired', 'pending',
    // 'slow_down', or 'retry' (detail is the error message on retry). Returns a
    // stop function that cancels the timer.
    function poll(base, client, token, userId, seconds, onStatus) {
        var delay = Math.max(Number(seconds) || 5, 5);
        var timer = null;
        var stopped = false;

        function stop() {
            stopped = true;
            if (timer) {
                clearTimeout(timer);
                timer = null;
            }
        }

        async function tick() {
            if (stopped) {
                return;
            }
            try {
                var status = (await request(base, client, token, '/Refract/Users/' + userId + '/PollAuthorizationStatus')).status;
                if (status === 'authorized' || status === 'denied' || status === 'expired') {
                    stop();
                    onStatus(status);
                    return;
                }
                if (status === 'slow_down') {
                    delay += 5;
                }
                onStatus(status);
            } catch (error) {
                delay += 5;
                onStatus('retry', error.message);
            }
            if (!stopped) {
                timer = setTimeout(tick, delay * 1000);
            }
        }

        tick();
        return stop;
    }

    return { headers: headers, request: request, poll: poll };
}());
