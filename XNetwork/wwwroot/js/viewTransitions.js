(function () {
    const sameDocumentNavSelector = 'a[href]:not([target]):not([download])';
    let transitionInProgress = false;

    function prefersReducedMotion() {
        return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    }

    function supportsViewTransitions() {
        return Boolean(document.startViewTransition) && !prefersReducedMotion();
    }

    function isModifiedClick(event) {
        return event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey;
    }

    function findAnchor(event) {
        if (typeof event.composedPath === 'function') {
            const anchor = event.composedPath().find(element => element instanceof HTMLAnchorElement);
            if (anchor) {
                return anchor;
            }
        }

        return event.target instanceof Element ? event.target.closest(sameDocumentNavSelector) : null;
    }

    function getAppPath(url) {
        return `${url.pathname}${url.search}${url.hash}`;
    }

    function shouldTransition(anchor) {
        if (!anchor.matches(sameDocumentNavSelector) || anchor.closest('#blazor-error-ui')) {
            return false;
        }

        const url = new URL(anchor.href, document.baseURI);
        if (url.origin !== window.location.origin || !['http:', 'https:'].includes(url.protocol)) {
            return false;
        }

        return getAppPath(url) !== getAppPath(window.location);
    }

    function nextFrame() {
        return new Promise(resolve => requestAnimationFrame(resolve));
    }

    document.addEventListener('click', event => {
        const anchor = findAnchor(event);
        if (!anchor || !shouldTransition(anchor)) {
            return;
        }

        if (isModifiedClick(event) || !supportsViewTransitions() || !window.Blazor?.navigateTo) {
            return;
        }

        if (transitionInProgress) {
            event.preventDefault();
            return;
        }

        event.preventDefault();
        event.stopImmediatePropagation();
        transitionInProgress = true;

        const targetPath = getAppPath(new URL(anchor.href, document.baseURI));
        const transition = document.startViewTransition(async () => {
            window.Blazor.navigateTo(targetPath);
            await nextFrame();
            await nextFrame();
        });

        transition.finished.finally(() => {
            transitionInProgress = false;
        });
    }, true);
})();
