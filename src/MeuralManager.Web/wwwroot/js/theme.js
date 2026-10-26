// Applies the color theme picked in Settings: "Dark"/"Light" set <html data-theme>, "System" removes
// it so app.css follows prefers-color-scheme. The choice is also kept in this browser's
// localStorage, so the inline script in App.razor can apply it on the next page load, before
// sign-in has loaded the account's setting.
window.meuralTheme = {
    apply(theme) {
        const value = theme === "Dark" ? "dark" : theme === "Light" ? "light" : null;
        if (value)
            document.documentElement.setAttribute("data-theme", value);
        else
            document.documentElement.removeAttribute("data-theme");
        try {
            if (value)
                localStorage.setItem("meuralmanager.theme", value);
            else
                localStorage.removeItem("meuralmanager.theme");
        } catch {
            // Storage blocked - the theme still applies for this page.
        }
    }
};
