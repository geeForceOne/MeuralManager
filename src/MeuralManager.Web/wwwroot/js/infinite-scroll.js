// Fires ImmichPhotoGrid's OnSentinelVisible when its "load more" sentinel scrolls into view (or
// within a screenful of it), so the timeline keeps loading as you scroll instead of needing a
// button click per page.
//
// watch() is called on every render while there's more to load: disconnecting and re-observing
// makes IntersectionObserver deliver an immediate "already intersecting" callback, which is what
// keeps loading when a freshly-loaded page still doesn't fill the screen - a plain observer would
// only fire on the *change* into view and stall there.
window.meuralInfiniteScroll = (function () {
    const observers = new WeakMap();

    return {
        watch: function (sentinelEl, dotNetRef) {
            if (!sentinelEl) {
                return;
            }
            const previous = observers.get(sentinelEl);
            if (previous) {
                previous.disconnect();
            }

            const observer = new IntersectionObserver(entries => {
                if (entries.some(e => e.isIntersecting)) {
                    observer.disconnect();
                    dotNetRef.invokeMethodAsync('OnSentinelVisible');
                }
            }, { rootMargin: '600px 0px' });

            observer.observe(sentinelEl);
            observers.set(sentinelEl, observer);
        },

        stop: function (sentinelEl) {
            const observer = sentinelEl && observers.get(sentinelEl);
            if (observer) {
                observer.disconnect();
            }
        },
    };
})();
