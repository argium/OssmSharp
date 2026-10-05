const storageKey = "ossm-dashboard-theme";

/** Applies the saved dashboard theme and wires the theme control. */
export function initializeTheme() {
  const root = document.documentElement;
  let savedTheme;
  try {
    savedTheme = localStorage.getItem(storageKey);
  } catch {
    // Storage can be unavailable in private or embedded browsing contexts.
  }
  root.dataset.theme = savedTheme === "light" || savedTheme === "dark" ? savedTheme : "dark";

  document.addEventListener("click", (event) => {
    if (!(event.target instanceof Element) || !event.target.closest("[data-theme-toggle]")) return;
    root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
    try {
      localStorage.setItem(storageKey, root.dataset.theme);
    } catch {
      // The selected theme still applies for this page.
    }
  });
}
