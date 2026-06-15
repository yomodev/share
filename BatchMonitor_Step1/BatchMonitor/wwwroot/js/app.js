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

/**
 * Width reserved for the overflow "▼" button. Always subtracted from the
 * available width up-front once any tab needs to overflow, so showing/hiding
 * the button never changes the fitting calculation (avoids oscillation).
 */
window.BatchMonitor.OVERFLOW_BTN_WIDTH = 32;
window.BatchMonitor.MIN_TAB_WIDTH = 90;
window.BatchMonitor.MAX_TAB_WIDTH = 190;

/**
 * Computes the full tab-bar layout: which tabs are visible (and at what
 * width, possibly shrunk below MAX_TAB_WIDTH) and which overflow into the
 * "▼" menu.
 *
 * Policy: rightmost-first overflow (newest/rightmost tabs hide first),
 * shrink-before-hide (visible tabs shrink toward MIN_TAB_WIDTH before any
 * tab is pushed to overflow), active tab is always exempt from overflow.
 *
 * containerEl   - the visible/clipping strip (.bm-tabs-scroll)
 * measureEl     - offscreen strip containing one always-laid-out copy of every
 *                  tab (data-measure-tab-id), in current display order
 * activeTabId   - id of the currently active tab (never overflows)
 * overflowShown - whether the overflow button is currently rendered
 *
 * Returns { overflowingIds: string[], widths: { [id]: number } }
 * `widths` contains an entry for every VISIBLE tab (px width to apply).
 * Tabs not present in `widths` are overflowing.
 */
window.BatchMonitor.computeTabLayout = (containerEl, measureEl, activeTabId, overflowShown) => {
    const MIN = window.BatchMonitor.MIN_TAB_WIDTH;
    const MAX = window.BatchMonitor.MAX_TAB_WIDTH;
    const BTN = window.BatchMonitor.OVERFLOW_BTN_WIDTH;

    try {
        if (!containerEl || !measureEl) return { overflowingIds: [], widths: {} };

        // Width available if NO overflow button were shown.
        let available = containerEl.clientWidth;
        if (overflowShown) available += BTN;

        const items = Array.from(measureEl.querySelectorAll('[data-measure-tab-id]'));
        if (items.length === 0) return { overflowingIds: [], widths: {} };

        const ids = items.map(el => el.getAttribute('data-measure-tab-id'));
        // Ideal width per tab: natural content width, clamped to [MIN, MAX].
        const ideal = items.map(el => {
            const w = el.getBoundingClientRect().width;
            return Math.min(MAX, Math.max(MIN, w));
        });

        const activeIdx = ids.indexOf(activeTabId);

        // Try fitting a given ordered subset of indices (in their original
        // relative order) within `budget`, shrinking proportionally toward
        // MIN if needed. Returns the per-tab width map if it fits, else null.
        const tryFit = (indices, budget) => {
            const total = indices.reduce((s, i) => s + ideal[i], 0);
            if (total <= budget + 1) {
                // Fits at ideal widths, no shrink needed.
                const widths = {};
                indices.forEach(i => widths[ids[i]] = ideal[i]);
                return widths;
            }

            // Shrink proportionally: each tab gives up width proportional to
            // its slack above MIN, until total == budget (or all at MIN).
            const totalSlack = indices.reduce((s, i) => s + (ideal[i] - MIN), 0);
            const overBy = total - budget;
            if (totalSlack <= 0) return null; // already all at MIN, still doesn't fit

            const shrinkRatio = Math.min(1, overBy / totalSlack);
            const widths = {};
            let sum = 0;
            indices.forEach(i => {
                const slack = ideal[i] - MIN;
                const w = ideal[i] - slack * shrinkRatio;
                widths[ids[i]] = w;
                sum += w;
            });

            return sum <= budget + 1 ? widths : null;
        };

        // Start with everything visible, drop rightmost (excluding active)
        // one at a time until it fits.
        let visibleIdx = ids.map((_, i) => i);
        let overflowIdx = [];
        let widths = tryFit(visibleIdx, available);

        if (widths === null) {
            // Need the overflow button: re-budget.
            const budget = available - BTN;
            widths = tryFit(visibleIdx, budget);

            while (widths === null && visibleIdx.length > 1) {
                // Find rightmost non-active index still visible.
                let dropAt = -1;
                for (let k = visibleIdx.length - 1; k >= 0; k--) {
                    if (visibleIdx[k] !== activeIdx) { dropAt = k; break; }
                }
                if (dropAt === -1) break; // only the active tab left

                overflowIdx.unshift(visibleIdx[dropAt]);
                visibleIdx.splice(dropAt, 1);
                widths = tryFit(visibleIdx, budget);
            }

            if (widths === null) widths = {}; // degenerate: nothing fits, show nothing
        }

        const overflowingIds = overflowIdx.map(i => ids[i]);
        return { overflowingIds, widths };
    } catch {
        return { overflowingIds: [], widths: {} };
    }
};


/**
 * Sets up a ResizeObserver on the tab strip container that invokes a Blazor
 * method whenever the size changes, so overflow can be recomputed.
 * Returns a handle object with a `dispose()` method.
 */
window.BatchMonitor.observeTabBarResize = (containerEl, dotnetHelper, methodName) => {
    if (!containerEl) return { dispose: () => {} };
    let frame = null;
    const ro = new ResizeObserver(() => {
        if (frame) cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            dotnetHelper.invokeMethodAsync(methodName);
        });
    });
    ro.observe(containerEl);
    return {
        dispose: () => { ro.disconnect(); if (frame) cancelAnimationFrame(frame); }
    };
};

/**
 * Given a pointer clientX during a drag-over of the tab strip, returns the
 * index at which the dragged tab would be inserted if dropped now (0-based,
 * relative to the currently VISIBLE tabs in DOM order). Used to position the
 * drop indicator and to compute the target index passed to TabService.Reorder.
 *
 * containerEl - the visible tab strip (.bm-tabs-scroll)
 * clientX     - pointer X from the dragover event
 */
window.BatchMonitor.getTabDropIndex = (containerEl, clientX) => {
    try {
        if (!containerEl) return 0;
        const items = Array.from(containerEl.querySelectorAll('[data-tab-id]'))
            .filter(el => el.offsetParent !== null); // visible only

        for (let i = 0; i < items.length; i++) {
            const rect = items[i].getBoundingClientRect();
            const mid = rect.left + rect.width / 2;
            if (clientX < mid) return i;
        }
        return items.length;
    } catch {
        return 0;
    }
};
