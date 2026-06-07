export function getSlideConfirmMetrics(trackId, handleId) {
    const track = document.getElementById(trackId);
    const handle = document.getElementById(handleId);
    const computedStyle = track ? getComputedStyle(track) : null;
    const insetValue = computedStyle?.getPropertyValue('--slide-handle-inset') || '0px';
    const insetPx = parseCssLengthToPx(insetValue, track);

    return {
        TrackWidth: track?.getBoundingClientRect().width ?? 1,
        ThumbWidth: handle?.getBoundingClientRect().width ?? 1,
        InsetPx: insetPx
    };
}

function parseCssLengthToPx(value, contextElement) {
    const trimmed = value.trim();
    if (!trimmed) {
        return 0;
    }

    if (trimmed.endsWith('px')) {
        return Number.parseFloat(trimmed) || 0;
    }

    const probe = document.createElement('span');
    probe.style.position = 'absolute';
    probe.style.visibility = 'hidden';
    probe.style.width = trimmed;
    (contextElement || document.body).appendChild(probe);
    const pixels = probe.getBoundingClientRect().width;
    probe.remove();
    return Number.isFinite(pixels) ? pixels : 0;
}

export function captureSlideConfirmPointer(handleId, pointerId) {
    const handle = document.getElementById(handleId);
    if (handle && typeof handle.setPointerCapture === 'function') {
        try {
            handle.setPointerCapture(pointerId);
        } catch {
            // Pointer capture can fail if the pointer was already released.
        }
    }
}

export function releaseSlideConfirmPointer(handleId, pointerId) {
    const handle = document.getElementById(handleId);
    if (handle && typeof handle.releasePointerCapture === 'function') {
        try {
            handle.releasePointerCapture(pointerId);
        } catch {
            // Pointer capture can already be released by the browser.
        }
    }
}
