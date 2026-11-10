// The browser's offset from UTC, in minutes (positive west of Greenwich, as getTimezoneOffset
// gives it). The server runs in the container's time zone - usually UTC - so pages that show or
// take a clock time (the Message page's "until") convert with the viewer's own offset.
window.meuralLocalTime = {
    offsetMinutes: () => new Date().getTimezoneOffset()
};
