const idleCompactDelayMs = 5000;
const scrollNoiseThresholdPx = 4;
const routeOrder = ["/", "/details", "/xbond", "/xrouter", "/settings"];
const routeAliases = new Map([
    ["/wifi", "/xrouter"]
]);

export function start(dotNetReference) {
    let disposed = false;
    let expanded = false;
    let idleTimer = null;
    let lastScrollY = getScrollY();
    let activeRubberCard = null;
    let rubberStartY = 0;
    let rubberReleaseTimer = null;

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

    const handleRouteDirection = event => {
        if (!(event.target instanceof Element)) {
            return;
        }

        const anchor = event.target.closest("a[href]");
        if (!(anchor instanceof HTMLAnchorElement)) {
            return;
        }

        const currentIndex = getRouteIndex(window.location.href);
        const targetIndex = getRouteIndex(anchor.href);
        if (currentIndex < 0 || targetIndex < 0 || currentIndex === targetIndex) {
            return;
        }

        document.documentElement.dataset.routeDirection = targetIndex > currentIndex ? "forward" : "back";
    };

    const handleRubberStart = event => {
        if (prefersReducedMotion() || !(event.target instanceof Element)) {
            return;
        }

        const card = event.target.closest(".rubber-card");
        if (!(card instanceof HTMLElement)) {
            return;
        }

        activeRubberCard = card;
        rubberStartY = getClientY(event);
        if (rubberReleaseTimer !== null) {
            window.clearTimeout(rubberReleaseTimer);
            rubberReleaseTimer = null;
        }

        activeRubberCard.classList.remove("rubber-card-releasing");
        activeRubberCard.classList.add("rubber-card-dragging");
    };

    const handleRubberMove = event => {
        if (!activeRubberCard) {
            return;
        }

        const delta = clamp((getClientY(event) - rubberStartY) * 0.12, -10, 10);
        activeRubberCard.style.setProperty("--rubber-y", `${delta}px`);
        activeRubberCard.style.setProperty("--rubber-scale", `${1 - Math.min(Math.abs(delta) / 900, 0.01)}`);
    };

    const handleRubberEnd = () => {
        if (!activeRubberCard) {
            return;
        }

        const card = activeRubberCard;
        activeRubberCard = null;
        card.classList.remove("rubber-card-dragging");
        card.classList.add("rubber-card-releasing");
        card.style.setProperty("--rubber-y", "0px");
        card.style.setProperty("--rubber-scale", "1");
        rubberReleaseTimer = window.setTimeout(() => {
            card.classList.remove("rubber-card-releasing");
            rubberReleaseTimer = null;
        }, 460);
    };

    const syncScrollOrigin = () => {
        lastScrollY = getScrollY();
    };

    const handlePointerDown = event => {
        syncScrollOrigin();
        handleNavInteraction(event);
        handleRouteDirection(event);
        handleRubberStart(event);
    };

    window.addEventListener("scroll", handleScroll, { passive: true });
    window.addEventListener("wheel", syncScrollOrigin, { passive: true });
    window.addEventListener("touchstart", syncScrollOrigin, { passive: true });
    window.addEventListener("pointerdown", handlePointerDown, { passive: true });
    window.addEventListener("pointermove", handleRubberMove, { passive: true });
    window.addEventListener("pointerup", handleRubberEnd, { passive: true });
    window.addEventListener("pointercancel", handleRubberEnd, { passive: true });
    window.addEventListener("click", handleRouteDirection, { capture: true, passive: true });
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
            window.removeEventListener("pointermove", handleRubberMove);
            window.removeEventListener("pointerup", handleRubberEnd);
            window.removeEventListener("pointercancel", handleRubberEnd);
            window.removeEventListener("click", handleRouteDirection, { capture: true });
            window.removeEventListener("focusin", handleNavInteraction);
            if (rubberReleaseTimer !== null) {
                window.clearTimeout(rubberReleaseTimer);
            }
        }
    };
}

function getScrollY() {
    return window.scrollY || document.documentElement.scrollTop || document.body.scrollTop || 0;
}

function getRouteIndex(url) {
    try {
        const path = routeAliases.get(new URL(url, window.location.origin).pathname) ?? new URL(url, window.location.origin).pathname;
        return routeOrder.indexOf(path);
    } catch {
        return -1;
    }
}

function getClientY(event) {
    if (typeof TouchEvent !== "undefined" && event instanceof TouchEvent && event.touches.length > 0) {
        return event.touches[0].clientY;
    }

    return event.clientY ?? 0;
}

function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
}

function prefersReducedMotion() {
    return window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches === true;
}
