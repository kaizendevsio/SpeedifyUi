const idleCompactDelayMs = 5000;
const scrollNoiseThresholdPx = 4;

export function start(dotNetReference) {
    let disposed = false;
    let expanded = false;
    let idleTimer = null;
    let lastScrollY = getScrollY();

    const emit = expandedValue => {
        if (disposed || expanded === expandedValue) {
            return;
        }

        expanded = expandedValue;
        dotNetReference.invokeMethodAsync("SetMobileTabbarExpanded", expandedValue)
            .catch(() => {
                // The Blazor circuit may disconnect while the page is unloading.
            });
    };

    const clearIdleTimer = () => {
        if (idleTimer !== null) {
            window.clearTimeout(idleTimer);
            idleTimer = null;
        }
    };

    const scheduleIdleCompact = () => {
        clearIdleTimer();
        idleTimer = window.setTimeout(() => emit(false), idleCompactDelayMs);
    };

    const expandTemporarily = () => {
        emit(true);
        scheduleIdleCompact();
    };

    const compactNow = () => {
        clearIdleTimer();
        emit(false);
    };

    const handleScroll = () => {
        const currentScrollY = getScrollY();
        const delta = currentScrollY - lastScrollY;

        if (Math.abs(delta) < scrollNoiseThresholdPx) {
            return;
        }

        if (delta < 0) {
            expandTemporarily();
        } else {
            compactNow();
        }

        lastScrollY = currentScrollY;
    };

    const handleNavInteraction = event => {
        if (event.target instanceof Element && event.target.closest(".mobile-tabbar")) {
            expandTemporarily();
        }
    };

    const syncScrollOrigin = () => {
        lastScrollY = getScrollY();
    };

    const handlePointerDown = event => {
        syncScrollOrigin();
        handleNavInteraction(event);
    };

    window.addEventListener("scroll", handleScroll, { passive: true });
    window.addEventListener("wheel", syncScrollOrigin, { passive: true });
    window.addEventListener("touchstart", syncScrollOrigin, { passive: true });
    window.addEventListener("pointerdown", handlePointerDown, { passive: true });
    window.addEventListener("focusin", handleNavInteraction);

    compactNow();

    return {
        dispose() {
            disposed = true;
            clearIdleTimer();
            window.removeEventListener("scroll", handleScroll);
            window.removeEventListener("wheel", syncScrollOrigin);
            window.removeEventListener("touchstart", syncScrollOrigin);
            window.removeEventListener("pointerdown", handlePointerDown);
            window.removeEventListener("focusin", handleNavInteraction);
        }
    };
}

function getScrollY() {
    return window.scrollY || document.documentElement.scrollTop || document.body.scrollTop || 0;
}
