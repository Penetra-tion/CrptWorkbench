(function () {
    const themeKey = "crpt-workbench-theme";
    const sidebarKey = "crpt-workbench-sidebar-collapsed";

    function preferredTheme() {
        const saved = localStorage.getItem(themeKey);
        if (saved === "light" || saved === "dark") return saved;
        return window.matchMedia?.("(prefers-color-scheme: light)").matches ? "light" : "dark";
    }

    function applyTheme(theme) {
        document.documentElement.dataset.theme = theme;
        document.documentElement.style.colorScheme = theme;
    }

    const initialTheme = preferredTheme();
    applyTheme(initialTheme);
    document.documentElement.classList.toggle("sidebar-initially-collapsed", localStorage.getItem(sidebarKey) === "true");

    window.crptWorkbench = {
        getAppearance: function () {
            return {
                theme: document.documentElement.dataset.theme || initialTheme,
                sidebarCollapsed: localStorage.getItem(sidebarKey) === "true"
            };
        },
        toggleTheme: function () {
            const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
            localStorage.setItem(themeKey, next);
            applyTheme(next);
            return next;
        },
        saveSidebar: function (collapsed) {
            localStorage.setItem(sidebarKey, collapsed ? "true" : "false");
            document.documentElement.classList.toggle("sidebar-initially-collapsed", collapsed);
        },
        getLocalFlag: function (key) {
            return localStorage.getItem(key) === "true";
        },
        setLocalFlag: function (key, value) {
            localStorage.setItem(key, value ? "true" : "false");
        },
        copyText: async function (value) {
            await navigator.clipboard.writeText(value || "");
        }
    };
})();
