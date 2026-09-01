// Crop tool for CropDialog.razor. The whole image is shown at its natural "fit the dialog"
// size (a plain <img>, not a canvas) and a thin, aspect-locked frame div sits on top of it -
// drag the frame to reposition it, scroll to resize it. Only on Apply does a source rectangle
// get read out of the image and drawn to an offscreen canvas for export.
window.meuralCropper = (function () {
    const MIN_ZOOM_FACTOR = 0.15; // frame can shrink to this fraction of its max-fit size
    const TARGET_LONG_EDGE = 1600;

    // Per-dialog state, keyed by the frame element so it's garbage-collected together with it
    // when CropDialog's @if(_visible) block unmounts the dialog.
    const states = new WeakMap();

    function outputSize(aspectW, aspectH) {
        return aspectW >= aspectH
            ? { width: TARGET_LONG_EDGE, height: Math.round(TARGET_LONG_EDGE * aspectH / aspectW) }
            : { width: Math.round(TARGET_LONG_EDGE * aspectW / aspectH), height: TARGET_LONG_EDGE };
    }

    function clamp(value, min, max) {
        return Math.min(Math.max(value, min), max);
    }

    // The largest frame of the given aspect ratio that fits entirely inside the rendered image.
    function maxFitSize(state, aspectW, aspectH) {
        let width = state.imgWidth;
        let height = width * aspectH / aspectW;
        if (height > state.imgHeight) {
            height = state.imgHeight;
            width = height * aspectW / aspectH;
        }
        return { width, height };
    }

    function applyFrameStyle(state) {
        state.frameEl.style.left = state.frame.left + 'px';
        state.frameEl.style.top = state.frame.top + 'px';
        state.frameEl.style.width = state.frame.width + 'px';
        state.frameEl.style.height = state.frame.height + 'px';
    }

    function clampFramePosition(state) {
        state.frame.left = clamp(state.frame.left, state.imgLeft, state.imgLeft + state.imgWidth - state.frame.width);
        state.frame.top = clamp(state.frame.top, state.imgTop, state.imgTop + state.imgHeight - state.frame.height);
    }

    function measureImage(state) {
        const stageRect = state.stageEl.getBoundingClientRect();
        const imgRect = state.imgEl.getBoundingClientRect();
        state.imgLeft = imgRect.left - stageRect.left;
        state.imgTop = imgRect.top - stageRect.top;
        state.imgWidth = imgRect.width;
        state.imgHeight = imgRect.height;
    }

    function setFrameForAspect(state, aspectW, aspectH, scaleOfMax) {
        state.aspectW = aspectW;
        state.aspectH = aspectH;
        const maxSize = maxFitSize(state, aspectW, aspectH);
        const width = maxSize.width * scaleOfMax;
        const height = width * aspectH / aspectW;
        const priorCenterX = state.frame ? state.frame.left + state.frame.width / 2 : state.imgLeft + state.imgWidth / 2;
        const priorCenterY = state.frame ? state.frame.top + state.frame.height / 2 : state.imgTop + state.imgHeight / 2;
        state.frame = {
            width,
            height,
            left: priorCenterX - width / 2,
            top: priorCenterY - height / 2,
        };
        clampFramePosition(state);
        applyFrameStyle(state);
    }

    function onWheel(state, e) {
        e.preventDefault();
        const maxSize = maxFitSize(state, state.aspectW, state.aspectH);
        const minSize = { width: maxSize.width * MIN_ZOOM_FACTOR, height: maxSize.height * MIN_ZOOM_FACTOR };

        const factor = e.deltaY < 0 ? 0.92 : 1 / 0.92; // scroll up = shrink (zoom in/tighter crop)
        const cx = state.frame.left + state.frame.width / 2;
        const cy = state.frame.top + state.frame.height / 2;

        const width = clamp(state.frame.width * factor, minSize.width, maxSize.width);
        const height = width * state.aspectH / state.aspectW;

        state.frame = { width, height, left: cx - width / 2, top: cy - height / 2 };
        clampFramePosition(state);
        applyFrameStyle(state);
    }

    function onPointerDown(state, e) {
        state.dragging = true;
        state.dragStartX = e.clientX;
        state.dragStartY = e.clientY;
        state.dragStartLeft = state.frame.left;
        state.dragStartTop = state.frame.top;
        state.frameEl.setPointerCapture(e.pointerId);
        state.frameEl.style.cursor = 'grabbing';
    }

    function onPointerMove(state, e) {
        if (!state.dragging) {
            return;
        }
        state.frame.left = state.dragStartLeft + (e.clientX - state.dragStartX);
        state.frame.top = state.dragStartTop + (e.clientY - state.dragStartY);
        clampFramePosition(state);
        applyFrameStyle(state);
    }

    function onPointerUp(state, e) {
        state.dragging = false;
        state.frameEl.style.cursor = 'grab';
        try { state.frameEl.releasePointerCapture(e.pointerId); } catch { /* already released */ }
    }

    function attachResizeObserver(state) {
        const ro = new ResizeObserver(() => {
            if (!state.frame || state.imgWidth === 0 || state.imgHeight === 0) {
                return;
            }
            // Preserve the frame's coverage as a fraction of the image, not raw pixels, so a
            // dialog/window resize doesn't leave the frame pointing at the wrong spot.
            const fracLeft = (state.frame.left - state.imgLeft) / state.imgWidth;
            const fracTop = (state.frame.top - state.imgTop) / state.imgHeight;
            const fracW = state.frame.width / state.imgWidth;
            const fracH = state.frame.height / state.imgHeight;

            measureImage(state);
            if (state.imgWidth === 0 || state.imgHeight === 0) {
                return;
            }

            state.frame = {
                left: state.imgLeft + fracLeft * state.imgWidth,
                top: state.imgTop + fracTop * state.imgHeight,
                width: fracW * state.imgWidth,
                height: fracH * state.imgHeight,
            };
            clampFramePosition(state);
            applyFrameStyle(state);
        });
        ro.observe(state.stageEl);
        state.resizeObserver = ro;
    }

    return {
        init: function (stageEl, imgEl, frameEl, aspectW, aspectH) {
            return new Promise((resolve, reject) => {
                function ready() {
                    const state = { stageEl, imgEl, frameEl, dragging: false };
                    measureImage(state);
                    if (state.imgWidth === 0 || state.imgHeight === 0) {
                        reject(new Error('Image has no visible size.'));
                        return;
                    }

                    // Starts maximized - the largest frame of this aspect that fits the image,
                    // centered - so the default crop keeps as much of the picture as possible;
                    // scroll to shrink it from there.
                    setFrameForAspect(state, aspectW, aspectH, 1);
                    states.set(frameEl, state);

                    stageEl.addEventListener('wheel', e => onWheel(state, e), { passive: false });
                    frameEl.addEventListener('pointerdown', e => onPointerDown(state, e));
                    frameEl.addEventListener('pointermove', e => onPointerMove(state, e));
                    frameEl.addEventListener('pointerup', e => onPointerUp(state, e));
                    frameEl.addEventListener('pointercancel', e => onPointerUp(state, e));
                    frameEl.style.cursor = 'grab';

                    attachResizeObserver(state);
                    resolve();
                }

                if (imgEl.complete && imgEl.naturalWidth > 0) {
                    ready();
                } else {
                    imgEl.onload = ready;
                    imgEl.onerror = () => reject(new Error('Failed to load image for cropping.'));
                }
            });
        },

        setAspect: function (frameEl, aspectW, aspectH) {
            const state = states.get(frameEl);
            if (!state) {
                return;
            }
            // Maximized for the new ratio too, same as the initial frame - "always maximized by
            // default" applies whenever the frame's aspect changes, not just on first open.
            setFrameForAspect(state, aspectW, aspectH, 1);
        },

        exportCrop: function (imgEl, frameEl) {
            return new Promise((resolve, reject) => {
                const state = states.get(frameEl);
                if (!state) {
                    reject(new Error('Crop not initialized.'));
                    return;
                }

                const scaleX = imgEl.naturalWidth / state.imgWidth;
                const scaleY = imgEl.naturalHeight / state.imgHeight;
                const sx = (state.frame.left - state.imgLeft) * scaleX;
                const sy = (state.frame.top - state.imgTop) * scaleY;
                const sWidth = state.frame.width * scaleX;
                const sHeight = state.frame.height * scaleY;

                const size = outputSize(state.aspectW, state.aspectH);
                const canvas = document.createElement('canvas');
                canvas.width = size.width;
                canvas.height = size.height;
                const ctx = canvas.getContext('2d');
                ctx.drawImage(imgEl, sx, sy, sWidth, sHeight, 0, 0, size.width, size.height);

                canvas.toBlob(function (blob) {
                    if (!blob) {
                        reject(new Error('Could not export the cropped image.'));
                        return;
                    }
                    // Resolve with the raw Blob, not DotNet.createJSStreamReference(blob) - that
                    // helper is for the opposite direction (JS calling a .NET method with a
                    // stream argument). When C# does JS.InvokeAsync<IJSStreamReference>(...),
                    // Blazor auto-wraps a raw Blob/ArrayBuffer/typed array returned from JS
                    // itself; wrapping it here too broke that auto-detection.
                    resolve(blob);
                }, 'image/jpeg', 0.92);
            });
        },

        dispose: function (frameEl) {
            const state = states.get(frameEl);
            if (state && state.resizeObserver) {
                state.resizeObserver.disconnect();
            }
        },

        // Used by the upload flow to hand the crop dialog a locally-picked file (before it's
        // ever been uploaded to Meural) without a server round trip - a blob: URL referencing
        // the File object straight out of the <input>'s FileList. Takes an element id (not an
        // ElementReference) since it's called with a plain string from Blazor.
        blobUrlForFile: function (elementId, index) {
            const el = document.getElementById(elementId);
            const file = el && el.files[index];
            return file ? URL.createObjectURL(file) : null;
        },

        revokeBlobUrl: function (url) {
            if (url) {
                URL.revokeObjectURL(url);
            }
        },
    };
})();
