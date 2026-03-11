// Batch Monitor — client-side utilities
// Minimal footprint; most logic is Blazor/C# side.

window.BatchMonitor = window.BatchMonitor || {};

/**
 * Reads a value from localStorage.
 * Called from C# via IJSRuntime when localStorage.getItem is too terse.
 */
window.BatchMonitor.getLocalStorage = (key) => {
    try { return localStorage.getItem(key); }
    catch { return null; }
};

/**
 * Writes a value to localStorage.
 */
window.BatchMonitor.setLocalStorage = (key, value) => {
    try { localStorage.setItem(key, value); }
    catch { /* quota exceeded or private browsing */ }
};

/**
 * Scrolls a tab strip so the active tab is visible.
 * Called after tab focus changes.
 */
window.BatchMonitor.scrollTabIntoView = (tabId) => {
    const el = document.querySelector(`[data-tab-id="${tabId}"]`);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
};
