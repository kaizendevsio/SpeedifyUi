(function (root, factory) {
    const api = factory();

    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    }

    if (root) {
        root.UlinkBlazorErrorRecovery = api;
    }
})(typeof window !== 'undefined' ? window : null, function () {
    const storageKey = 'ulink.blazor-error-recovery.v1';
    const options = {
        maxReloads: 2,
        retryWindowMs: 60_000,
        cooldownMs: 120_000,
        reloadDelayMs: 200,
        stableSessionMs: 20_000
    };

    let observer = null;
    let reloadTimerId = null;
    let stableTimerId = null;
    let handlingFatalError = false;

    function normalizeState(value) {
        const state = value && typeof value === 'object' ? value : {};
        return {
            attempts: Number.isFinite(state.attempts) ? Math.max(0, state.attempts) : 0,
            windowStartedAt: Number.isFinite(state.windowStartedAt) ? state.windowStartedAt : 0,
            cooldownUntil: Number.isFinite(state.cooldownUntil) ? state.cooldownUntil : 0
        };
    }

    function decideRecovery(value, now, recoveryOptions = options) {
        const state = normalizeState(value);

        if (state.cooldownUntil > now) {
            return { action: 'fallback', state };
        }

        if (state.cooldownUntil > 0 || state.windowStartedAt === 0 || now - state.windowStartedAt >= recoveryOptions.retryWindowMs) {
            state.attempts = 0;
            state.windowStartedAt = now;
            state.cooldownUntil = 0;
        }

        if (state.attempts >= recoveryOptions.maxReloads) {
            state.cooldownUntil = now + recoveryOptions.cooldownMs;
            return { action: 'fallback', state };
        }

        state.attempts += 1;
        return { action: 'reload', state };
    }

    function readState(storage) {
        try {
            return normalizeState(JSON.parse(storage.getItem(storageKey) || 'null'));
        } catch {
            return normalizeState(null);
        }
    }

    function writeState(storage, state) {
        try {
            storage.setItem(storageKey, JSON.stringify(normalizeState(state)));
        } catch {
            // A disabled sessionStorage must not prevent recovery.
        }
    }

    function clearState(storage) {
        try {
            storage.removeItem(storageKey);
        } catch {
            // A disabled sessionStorage is already equivalent to a cleared guard.
        }
    }

    function hideStockError(errorUi) {
        if (!errorUi) {
            return;
        }

        errorUi.setAttribute('aria-hidden', 'true');
        errorUi.style.setProperty('display', 'none', 'important');
    }

    function showFallback(documentObject) {
        const fallback = documentObject.getElementById('ulink-fatal-recovery');
        if (!fallback) {
            return;
        }

        fallback.hidden = false;
        fallback.setAttribute('aria-hidden', 'false');
        fallback.focus({ preventScroll: false });
    }

    function handleFatalError(reason = 'unhandled') {
        if (handlingFatalError || typeof window === 'undefined' || typeof document === 'undefined') {
            return;
        }

        handlingFatalError = true;
        document.documentElement.dataset.ulinkFatalRecovery = 'active';
        hideStockError(document.getElementById('blazor-error-ui'));

        if (stableTimerId !== null) {
            window.clearTimeout(stableTimerId);
            stableTimerId = null;
        }

        const decision = decideRecovery(readState(window.sessionStorage), Date.now());
        writeState(window.sessionStorage, decision.state);

        if (decision.action === 'fallback') {
            showFallback(document);
            console.error(`[uLink] Automatic page recovery paused after repeated ${reason} errors.`);
            return;
        }

        reloadTimerId = window.setTimeout(() => {
            reloadTimerId = null;
            window.location.reload();
        }, options.reloadDelayMs);
    }

    function isStockErrorVisible(errorUi) {
        if (!errorUi) {
            return false;
        }

        const inlineDisplay = errorUi.style?.getPropertyValue('display');
        return inlineDisplay && inlineDisplay !== 'none';
    }

    function start() {
        if (typeof document === 'undefined' || observer !== null) {
            return;
        }

        const errorUi = document.getElementById('blazor-error-ui');
        hideStockError(errorUi);
        if (!errorUi) {
            return;
        }

        observer = new MutationObserver(() => {
            if (isStockErrorVisible(errorUi)) {
                handleFatalError('unhandled');
            }
            hideStockError(errorUi);
        });
        observer.observe(errorUi, { attributes: true, attributeFilter: ['style', 'class'] });
    }

    function markSessionStarted() {
        if (typeof window === 'undefined') {
            return;
        }

        if (stableTimerId !== null) {
            window.clearTimeout(stableTimerId);
        }

        stableTimerId = window.setTimeout(() => {
            stableTimerId = null;
            clearState(window.sessionStorage);
            delete document.documentElement.dataset.ulinkFatalRecovery;
        }, options.stableSessionMs);
    }

    return {
        start,
        handleFatalError,
        markSessionStarted,
        decideRecovery,
        normalizeState,
        readState,
        writeState,
        clearState,
        storageKey,
        options
    };
});
