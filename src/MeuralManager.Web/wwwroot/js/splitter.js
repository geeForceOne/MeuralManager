// Minimal drag-to-resize for the three-pane layouts on the Playlists page (list | items | preview)
// and the Immich page (photos | basket | preview).
// By default each ".split-handle" resizes the pane immediately before it (dragging right grows
// it). Add data-resize="next" to a handle to have it resize the pane immediately after it
// instead (dragging left grows it) - used for the right-docked preview pane, so dragging its
// own left edge changes *its* width rather than the middle item grid's.
//
// A handle's pane is identified by paneKey - "list" for a resize-previous handle and "preview" for
// a resize-next one by default, or whatever data-pane-key says (the Immich page has two
// resize-next handles, so they name themselves "basket" and "preview").
//
// savedWidths (optional, {paneKey: px}) restores each pane to its last dragged width on init.
// dotNetRef (optional) gets OnSplitterResized(paneKey, widthPx) invoked once per drag, on
// mouseup, so the page can persist it - not on every mousemove, which would be far too chatty
// for a JS interop round trip.
//
// Returns false if the container isn't in the DOM yet (a page that renders its layout only once
// it has loaded something can call again after a later render), true once it's set up.
window.meuralSplitter = {
    init: function (containerId, dotNetRef, savedWidths) {
        const container = document.getElementById(containerId);
        if (!container) {
            return false;
        }
        if (container.dataset.splitterReady === "1") {
            return true;
        }
        container.dataset.splitterReady = "1";

        const minPane = 200;

        container.querySelectorAll(".split-handle").forEach(function (handle) {
            const resizeNext = handle.dataset.resize === "next";
            const pane = resizeNext ? handle.nextElementSibling : handle.previousElementSibling;
            if (!pane) {
                return;
            }

            const paneKey = handle.dataset.paneKey || (resizeNext ? "preview" : "list");
            const savedWidth = savedWidths && savedWidths[paneKey];
            if (savedWidth) {
                pane.style.flex = "0 0 " + savedWidth + "px";
            }

            let dragging = false;
            let startX = 0;
            let startWidth = 0;

            handle.addEventListener("mousedown", function (e) {
                dragging = true;
                startX = e.clientX;
                startWidth = pane.getBoundingClientRect().width;
                document.body.style.cursor = "col-resize";
                document.body.style.userSelect = "none";
                e.preventDefault();
            });

            window.addEventListener("mousemove", function (e) {
                if (!dragging) {
                    return;
                }
                const maxWidth = container.getBoundingClientRect().width - minPane * 2;
                const dx = e.clientX - startX;
                const delta = resizeNext ? -dx : dx;
                const newWidth = Math.max(minPane, Math.min(startWidth + delta, maxWidth));
                pane.style.flex = "0 0 " + newWidth + "px";
            });

            window.addEventListener("mouseup", function () {
                if (dragging) {
                    dragging = false;
                    document.body.style.cursor = "";
                    document.body.style.userSelect = "";
                    if (dotNetRef) {
                        dotNetRef.invokeMethodAsync("OnSplitterResized", paneKey, pane.getBoundingClientRect().width);
                    }
                }
            });
        });

        return true;
    }
};

window.meuralUtils = {
    focusAndSelect: function (el) {
        if (!el) {
            return;
        }
        el.focus();
        el.select();
    }
};
