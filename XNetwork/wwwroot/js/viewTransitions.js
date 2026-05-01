(function () {
    const sameDocumentNavSelector = 'a[href]:not([target]):not([download])';
    const pageSurfaceSelector = '.page-transition-surface';
    const renderTimeoutMs = 900;
    const cssExitMs = 120;
    const cssEnterMs = 240;
    let transitionInProgress = false;

    function prefersReducedMotion() {
        return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    }

    function supportsViewTransitions() {
        return Boolean(document.startViewTransition) && !prefersReducedMotion();
    }

    function isSafariLikeWebKit() {
        const userAgent = navigator.userAgent;
        const vendor = navigator.vendor || '';

        return /iPad|iPhone|iPod/.test(userAgent) || vendor.includes('Apple');
    }

    function shouldUseNativeViewTransition() {
        // Safari/WebKit can expose the API but often snapshots before Blazor Server finishes rendering.
        return supportsViewTransitions() && !isSafariLikeWebKit();
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

    function delay(milliseconds) {
        return new Promise(resolve => setTimeout(resolve, milliseconds));
    }

    function waitForRouteRender(surface) {
        return new Promise(resolve => {
            if (!surface) {
                nextFrame().then(resolve);
                return;
            }

            let resolved = false;
            let timeoutId;
            const observer = new MutationObserver(() => finish());

            function finish() {
                if (resolved) {
                    return;
                }

                resolved = true;
                clearTimeout(timeoutId);
                observer.disconnect();
                nextFrame().then(() => nextFrame().then(resolve));
            }

            observer.observe(surface, { childList: true });
            timeoutId = setTimeout(finish, renderTimeoutMs);
        });
    }

    async function navigateWithNativeTransition(targetPath) {
        const surface = document.querySelector(pageSurfaceSelector);
        const transition = document.startViewTransition(async () => {
            const renderPromise = waitForRouteRender(surface);
            window.Blazor.navigateTo(targetPath);
            await renderPromise;
        });

        await transition.finished;
    }

    async function navigateWithCssTransition(targetPath) {
        const surface = document.querySelector(pageSurfaceSelector);
        if (!surface || prefersReducedMotion()) {
            window.Blazor.navigateTo(targetPath);
            await nextFrame();
            return;
        }

        surface.classList.remove('page-transition-enter', 'page-transition-enter-active');
        surface.classList.add('page-transition-leave');
        await delay(cssExitMs);

        const renderPromise = waitForRouteRender(surface);
        window.Blazor.navigateTo(targetPath);
        await renderPromise;

        surface.classList.remove('page-transition-leave');
        surface.classList.add('page-transition-enter');
        await nextFrame();
        surface.classList.add('page-transition-enter-active');

        await delay(cssEnterMs);
        surface.classList.remove('page-transition-enter', 'page-transition-enter-active');
    }

    document.addEventListener('click', event => {
        const anchor = findAnchor(event);
        if (!anchor || !shouldTransition(anchor)) {
            return;
        }

        if (isModifiedClick(event) || !window.Blazor?.navigateTo) {
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
        const transition = shouldUseNativeViewTransition()
            ? navigateWithNativeTransition(targetPath)
            : navigateWithCssTransition(targetPath);

        transition
            .catch(error => console.debug('Page transition failed; navigation already attempted.', error))
            .finally(() => {
                transitionInProgress = false;
            });
    }, true);
})();
