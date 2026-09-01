// Minimal drag-to-resize for the Playlists page's three-pane layout (list | items | preview).
// By default each ".split-handle" resizes the pane immediately before it (dragging right grows
// it). Add data-resize="next" to a handle to have it resize the pane immediately after it
// instead (dragging left grows it) - used for the right-docked preview pane, so dragging its
// own left edge changes *its* width rather than the middle item grid's.
//
// savedWidths (optional, {list, preview} in px) restores each pane to its last dragged width on
// init. dotNetRef (optional) gets OnSplitterResized(paneKey, widthPx) invoked once per drag, on
// mouseup, so Playlists.razor can persist it - not on every mousemove, which would be far too
// chatty for a JS interop round trip.
window.meuralSplitter = {
    init: function (containerId, dotNetRef, savedWidths) {
        const container = document.getElementById(containerId);
        if (!container || container.dataset.splitterReady === "1") {
            return;
        }
        container.dataset.splitterReady = "1";

        const minPane = 200;

        container.querySelectorAll(".split-handle").forEach(function (handle) {
            const resizeNext = handle.dataset.resize === "next";
            const pane = resizeNext ? handle.nextElementSibling : handle.previousElementSibling;
            if (!pane) {
                return;
            }

            const paneKey = resizeNext ? "preview" : "list";
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
