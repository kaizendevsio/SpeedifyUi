const animatedNumbers = new WeakMap();
const autoAnimateInstances = new WeakMap();

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
