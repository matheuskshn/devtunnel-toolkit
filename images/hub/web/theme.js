"use strict";
(() => {
  const key = "devtunnel-toolkit-theme";
  const system = window.matchMedia("(prefers-color-scheme: dark)");
  const valid = (value) => value === "light" || value === "dark";
  let preference;
  try {
    preference = localStorage.getItem(key);
  } catch {
    // Restricted browser storage must not prevent using the console.
  }
  function apply() {
    const systemTheme = system.matches ? "dark" : "light";
    const theme = valid(preference) ? preference : systemTheme;
    document.documentElement.dataset.theme = theme;
    const label = theme === "dark" ? "Ativar modo claro" : "Ativar modo escuro";
    document.querySelectorAll("[data-theme-toggle]").forEach((button) => {
      button.setAttribute("aria-label", label);
      button.setAttribute("title", label);
    });
  }
  apply();
  document.addEventListener("click", (event) => {
    if (!event.target.closest?.("[data-theme-toggle]")) return;
    preference =
      document.documentElement.dataset.theme === "dark" ? "light" : "dark";
    try {
      localStorage.setItem(key, preference);
    } catch {
      // The selection still works for this page when storage is unavailable.
    }
    apply();
  });
  system.addEventListener("change", () => {
    if (!valid(preference)) apply();
  });
  window.addEventListener("storage", (event) => {
    if (event.key === key || event.key === null) {
      preference = event.newValue;
      apply();
    }
  });
})();
