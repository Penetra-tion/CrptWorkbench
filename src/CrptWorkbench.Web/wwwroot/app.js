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
        },
        downloadStream: async function (name, stream) {
            const data = await stream.arrayBuffer();
            const url = URL.createObjectURL(new Blob([data], { type: "text/plain;charset=utf-8" }));
            const link = document.createElement("a");
            link.href = url;
            link.download = name;
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        },
        downloadText: function (name, value) {
            const url = URL.createObjectURL(new Blob(["\uFEFF", value], { type: "text/plain;charset=utf-8" }));
            const link = document.createElement("a");
            link.href = url;
            link.download = name;
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
    };
})();
