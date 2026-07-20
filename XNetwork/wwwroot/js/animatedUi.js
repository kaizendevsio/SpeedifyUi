const animatedNumbers = new WeakMap();
const autoAnimateInstances = new WeakMap();
const flipListInstances = new WeakMap();

let autoAnimateModulePromise;

export function prefersReducedMotion() {
    return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

function getOdometerFormat(decimals) {
    if (decimals <= 0) {
        return "(,ddd)";
    }

    return `(,ddd).${"d".repeat(decimals)}`;
}

function getDecimalPlaces(value) {
    const decimals = Number(value);
    if (!Number.isFinite(decimals)) {
        return 0;
    }

    return Math.max(0, Math.min(3, Math.trunc(decimals)));
}

function getDuration(value) {
    const duration = Number(value);
    if (!Number.isFinite(duration)) {
        return 300;
    }

    return Math.max(0, duration);
}

export function updateAnimatedNumber(element, value, options = {}) {
    if (!element) {
        return;
    }

    const numericValue = Number(value);
    if (!Number.isFinite(numericValue)) {
        element.textContent = "";
        animatedNumbers.delete(element);
        return;
    }

    const decimals = getDecimalPlaces(options.decimals);
    const textValue = numericValue.toFixed(decimals);
    const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    if (!window.Odometer || prefersReducedMotion) {
        element.textContent = textValue;
        animatedNumbers.delete(element);
        return;
    }

    const current = animatedNumbers.get(element);
    if (!current || current.decimals !== decimals) {
        element.innerHTML = "";
        const odometer = new window.Odometer({
            el: element,
            value: textValue,
            format: getOdometerFormat(decimals),
            duration: getDuration(options.duration),
            theme: "minimal"
        });

        animatedNumbers.set(element, { odometer, decimals });
        odometer.render?.();
        return;
    }

    current.odometer.update(textValue);
}

export function disposeAnimatedNumber(element) {
    if (!element) {
        return;
    }

    animatedNumbers.delete(element);
}

export async function enableAutoAnimate(element, options = {}) {
    if (!element || autoAnimateInstances.has(element)) {
        return;
    }

    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
        return;
    }

    try {
        autoAnimateModulePromise ??= import("/vendor/auto-animate/index.min.js");
        const autoAnimateModule = await autoAnimateModulePromise;
        const autoAnimate = autoAnimateModule.default;
        const controller = autoAnimate(element, {
            duration: 280,
            easing: "cubic-bezier(0.22, 1, 0.36, 1)",
            ...options
        });

        autoAnimateInstances.set(element, controller);
    } catch (error) {
        console.warn("AutoAnimate failed to initialize", error);
    }
}

export function disableAutoAnimate(element) {
    if (!element) {
        return;
    }

    const controller = autoAnimateInstances.get(element);
    controller?.disable?.();
    autoAnimateInstances.delete(element);
}

function measureFlipItems(element) {
    const rects = new Map();
    for (const item of element.querySelectorAll(":scope > [data-flip-key]")) {
        const key = item.dataset.flipKey;
        if (key) {
            rects.set(key, item.getBoundingClientRect());
        }
    }

    return rects;
}

export function enableFlipList(element) {
    if (!element || flipListInstances.has(element) || prefersReducedMotion()) {
        return;
    }

    const state = {
        rects: measureFlipItems(element),
        animations: new Map()
    };

    const observer = new MutationObserver(mutations => {
        if (!mutations.some(mutation => mutation.type === "childList")) {
            return;
        }

        const previousRects = state.rects;
        const currentRects = measureFlipItems(element);

        for (const item of element.querySelectorAll(":scope > [data-flip-key]")) {
            const key = item.dataset.flipKey;
            const previous = previousRects.get(key);
            const current = currentRects.get(key);
            if (!previous || !current) {
                continue;
            }

            const deltaX = previous.left - current.left;
            const deltaY = previous.top - current.top;
            if (Math.abs(deltaX) < 0.5 && Math.abs(deltaY) < 0.5) {
                continue;
            }

            state.animations.get(key)?.cancel();
            const animation = item.animate(
                [
                    { transform: `translate(${deltaX}px, ${deltaY}px)` },
                    { transform: "translate(0, 0)" }
                ],
                {
                    duration: 420,
                    easing: "cubic-bezier(0.16, 1, 0.3, 1)"
                });

            state.animations.set(key, animation);
            animation.finished
                .catch(() => {})
                .finally(() => {
                    if (state.animations.get(key) === animation) {
                        state.animations.delete(key);
                    }
                });
        }

        state.rects = currentRects;
    });

    observer.observe(element, { childList: true });
    flipListInstances.set(element, { observer, state });
}

export function disableFlipList(element) {
    const instance = flipListInstances.get(element);
    if (!instance) {
        return;
    }

    instance.observer.disconnect();
    for (const animation of instance.state.animations.values()) {
        animation.cancel();
    }

    flipListInstances.delete(element);
}
