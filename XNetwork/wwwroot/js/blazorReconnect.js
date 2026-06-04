(function () {
    const retryDelaysMs = [
        0,
        250,
        500,
        1000,
        1500,
        2000,
        3000,
        5000,
        8000,
        10000,
        15000
    ];

    const options = {
        keepAliveMs: 5000,
        serverTimeoutMs: 15000,
        maxRetries: 60,
        failedRetryDelayMs: 5000
    };

    let failedRetryTimerId = null;

    function retryIntervalMilliseconds(previousAttempts, maxRetries) {
        if (previousAttempts >= maxRetries) {
            return null;
        }

        return retryDelaysMs[Math.min(previousAttempts, retryDelaysMs.length - 1)];
    }

    function clearFailedRetryTimer() {
        if (failedRetryTimerId !== null) {
            window.clearTimeout(failedRetryTimerId);
            failedRetryTimerId = null;
        }
    }

    function scheduleFailedReconnect() {
        clearFailedRetryTimer();

        failedRetryTimerId = window.setTimeout(async () => {
            failedRetryTimerId = null;
            await reconnectNow({ reloadOnFailure: false });

            const reconnectModal = document.getElementById('components-reconnect-modal');
            if (reconnectModal?.classList.contains('components-reconnect-failed')) {
                scheduleFailedReconnect();
            }
        }, options.failedRetryDelayMs);
    }

    async function reconnectNow({ reloadOnFailure = true } = {}) {
        if (!window.Blazor || typeof window.Blazor.reconnect !== 'function') {
            if (reloadOnFailure) {
                window.location.reload();
            }
            return false;
        }

        try {
            const reconnected = await window.Blazor.reconnect();
            if (!reconnected && reloadOnFailure) {
                window.location.reload();
            }
            return reconnected;
        } catch (error) {
            console.warn('[XNetwork] Manual Blazor reconnect failed', error);
            if (reloadOnFailure) {
                window.location.reload();
            }
            return false;
        }
    }

    function wireReconnectUi() {
        if (document.documentElement.dataset.xnetworkReconnectWired === 'true') {
            return;
        }

        document.documentElement.dataset.xnetworkReconnectWired = 'true';

        document.getElementById('xnetwork-reconnect-retry')?.addEventListener('click', () => {
            clearFailedRetryTimer();
            void reconnectNow();
        });

        document.getElementById('xnetwork-reconnect-reload')?.addEventListener('click', () => {
            window.location.reload();
        });

        document.addEventListener('visibilitychange', () => {
            if (!document.hidden) {
                const reconnectModal = document.getElementById('components-reconnect-modal');
                if (reconnectModal?.classList.contains('components-reconnect-failed')) {
                    clearFailedRetryTimer();
                    void reconnectNow({ reloadOnFailure: false });
                    scheduleFailedReconnect();
                }
            }
        });

        document.addEventListener('components-reconnect-state-changed', event => {
            const reconnectState = typeof event.detail === 'string'
                ? event.detail
                : event.detail?.state;

            if (reconnectState === 'hide') {
                clearFailedRetryTimer();
                return;
            }

            if (reconnectState === 'failed') {
                scheduleFailedReconnect();
                return;
            }

            if (reconnectState === 'rejected') {
                clearFailedRetryTimer();
            }
        });
    }

    async function start() {
        wireReconnectUi();

        await window.Blazor.start({
            circuit: {
                configureSignalR: builder => {
                    builder.withServerTimeout(options.serverTimeoutMs)
                        .withKeepAliveInterval(options.keepAliveMs);
                },
                reconnectionOptions: {
                    maxRetries: options.maxRetries,
                    retryIntervalMilliseconds
                }
            }
        });
    }

    window.XNetworkBlazorReconnect = {
        start,
        reconnectNow,
        options
    };
})();
