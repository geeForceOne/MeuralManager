// Animates the logo (Components/Shared/BrandMark.razor). Three motions, picked at random for each
// play and never the same twice in a row:
//   swap       - the M slides out behind the mat and the next one slides in, like a frame changing artwork;
//   draw       - the M fades and is painted back in one stroke;
//   straighten - the frame slips crooked, hangs there, and is nudged level again.
// The motions themselves are keyframes in app.css (.brand-mark svg.play-*); this only decides when
// one plays. The sidebar and sign-in logos play one when hovered or focused; a mark with
// data-motion="once" (the sign-in screen) plays one shortly after it appears, and one with
// data-motion="loop" (a busy indicator) keeps playing for as long as it's on the page. Nothing
// moves when the user asks for reduced motion.
(() => {
    const MOTIONS = ["swap", "draw", "straighten"];
    const LOOP_PAUSE_MS = 450;
    const reduced = matchMedia("(prefers-reduced-motion: reduce)");
    const playing = new WeakMap(); // svg -> the motion it's playing right now
    const looping = new WeakSet();
    let lastMotion = null;

    function pickMotion() {
        const options = MOTIONS.filter(m => m !== lastMotion);
        lastMotion = options[Math.floor(Math.random() * options.length)];
        return lastMotion;
    }

    function play(svg) {
        if (reduced.matches || playing.has(svg)) return;
        const motion = pickMotion();
        playing.set(svg, motion);
        svg.classList.add("play-" + motion);
    }

    // Each motion animates exactly one element inside the svg, so one animationend ends the play.
    document.addEventListener("animationend", e => {
        const svg = e.target.closest?.(".brand-mark svg");
        const motion = svg && playing.get(svg);
        if (!motion || e.animationName !== "bm-" + motion) return;
        svg.classList.remove("play-" + motion);
        playing.delete(svg);
        if (looping.has(svg)) setTimeout(() => { if (svg.isConnected) play(svg); }, LOOP_PAUSE_MS);
    });

    // Sidebar and sign-in logo: one play per hover or keyboard focus.
    const onEnter = e => {
        const brand = e.target.closest?.(".brand, .auth-brand");
        // Moving between the logo's own parts isn't a new hover.
        if (!brand || (e.relatedTarget && brand.contains(e.relatedTarget))) return;
        const svg = brand.querySelector(".brand-mark svg");
        if (svg) play(svg);
    };
    document.addEventListener("mouseover", onEnter);
    document.addEventListener("focusin", onEnter);

    // Busy indicators and the sign-in logo come and go as Blazor renders; start each one that
    // appears. A "once" logo plays a single time per element, after a short pause.
    const welcomed = new WeakSet();
    const startMarked = () => {
        for (const svg of document.querySelectorAll('.brand-mark svg[data-motion="loop"]')) {
            if (looping.has(svg)) continue;
            looping.add(svg);
            play(svg);
        }
        for (const svg of document.querySelectorAll('.brand-mark svg[data-motion="once"]')) {
            if (welcomed.has(svg)) continue;
            welcomed.add(svg);
            setTimeout(() => { if (svg.isConnected) play(svg); }, 500);
        }
    };
    new MutationObserver(startMarked).observe(document.documentElement, { childList: true, subtree: true });
    // Loops stop on their own when motion is turned off (play() does nothing); restart them when it's back on.
    reduced.addEventListener?.("change", () => {
        if (reduced.matches) return;
        for (const svg of document.querySelectorAll('.brand-mark svg[data-motion="loop"]')) play(svg);
    });
    startMarked();
})();
