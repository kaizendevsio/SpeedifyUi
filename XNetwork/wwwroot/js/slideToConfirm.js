export function getSlideConfirmMetrics(trackId, handleId) {
    const track = document.getElementById(trackId);
    const handle = document.getElementById(handleId);

    return {
        TrackWidth: track?.getBoundingClientRect().width ?? 1,
        ThumbWidth: handle?.getBoundingClientRect().width ?? 1
    };
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
