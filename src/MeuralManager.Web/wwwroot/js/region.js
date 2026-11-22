// The browser's language and time zone, used once per account as the first guess for the region
// format and time zone in Settings.
window.meuralRegion = {
    browser() {
        let zone = "UTC";
        try {
            zone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
        } catch {
            // Very old browser - stay on UTC.
        }
        return { language: navigator.language || "en-US", timeZone: zone };
    }
};
