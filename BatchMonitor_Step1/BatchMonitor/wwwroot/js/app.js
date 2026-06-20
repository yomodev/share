// Batch Monitor — client-side utilities

// ── localStorage helpers ──────────────────────────────────────────────────

/** @param {string} key @returns {string|null} */
export function getLocalStorage(key) {
    try { return localStorage.getItem(key); } catch { return null; }
}

/** @param {string} key @param {string} value */
export function setLocalStorage(key, value) {
    try { localStorage.setItem(key, value); } catch { }
}

// ── Tab bar: horizontal scroll + JS-driven drag-and-drop ─────────────────
//
// Drag logic runs entirely in the browser:
//   - dragstart/dragover/dragleave/dragend: track state, show indicator,
//     reorder tabs visually in the DOM preview
//   - drop: call Blazor ONCE with (tabId, newIndex) to commit the order
//
// No Blazor round-trips during the drag itself, so no "unavailable" cursor
// flash and no shaking from repeated async re-renders.

/**
 * @param {HTMLElement} containerEl
 * @param {{ invokeMethodAsync: (method: string, ...args: any[]) => Promise<void> }} dotnetHelper
 */
export function initTabDrag(containerEl, dotnetHelper) {
    if (!containerEl) return;

    let dragId = null;
    let indicator = null;

    function getTabs() {
        return Array.from(containerEl.querySelectorAll('[data-tab-id]'));
    }

    function getDropIndex(clientX) {
        const tabs = getTabs().filter(t => t.getAttribute('data-tab-id') !== dragId);
        for (let i = 0; i < tabs.length; i++) {
            const rect = tabs[i].getBoundingClientRect();
            if (clientX < rect.left + rect.width / 2) return i;
        }
        return tabs.length;
    }

    function showIndicator(clientX) {
        if (!indicator) {
            indicator = document.createElement('div');
            indicator.className = 'bm-tab-drop-indicator';
        }

        const tabs = getTabs().filter(t => t.getAttribute('data-tab-id') !== dragId);
        const idx = getDropIndex(clientX);

        if (idx < tabs.length) {
            containerEl.insertBefore(indicator, tabs[idx]);
        } else {
            containerEl.appendChild(indicator);
        }
    }

    function removeIndicator() {
        indicator?.remove();
    }

    containerEl.addEventListener('dragstart', e => {
        const tab = e.target.closest('[data-tab-id]');
        if (!tab) return;
        dragId = tab.getAttribute('data-tab-id');
        tab.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        // Must set data for Firefox compatibility
        e.dataTransfer.setData('text/plain', dragId);
    });

    containerEl.addEventListener('dragover', e => {
        if (!dragId) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        showIndicator(e.clientX);
    });

    containerEl.addEventListener('dragleave', e => {
        // Only remove if leaving the container entirely
        if (!containerEl.contains(e.relatedTarget)) {
            removeIndicator();
        }
    });

    containerEl.addEventListener('dragend', e => {
        removeIndicator();
        const tab = containerEl.querySelector(`[data-tab-id="${dragId}"]`);
        tab?.classList.remove('dragging');
        dragId = null;
    });

    containerEl.addEventListener('drop', e => {
        e.preventDefault();
        if (!dragId) return;

        const allTabs = getTabs();
        const nonDragTabs = allTabs.filter(t => t.getAttribute('data-tab-id') !== dragId);
        const dropIdx = getDropIndex(e.clientX);

        // Absolute index in the full tabs list (including the dragged one
        // that Blazor will re-insert after removing it first).
        // nonDragTabs[dropIdx] is the tab currently at that visual slot.
        let targetIndex;
        if (dropIdx < nonDragTabs.length) {
            const targetId = nonDragTabs[dropIdx].getAttribute('data-tab-id');
            targetIndex = allTabs.findIndex(t => t.getAttribute('data-tab-id') === targetId);
        } else {
            targetIndex = allTabs.length - 1;
        }

        removeIndicator();
        const tab = containerEl.querySelector(`[data-tab-id="${dragId}"]`);
        tab?.classList.remove('dragging');

        const id = dragId;
        dragId = null;

        dotnetHelper.invokeMethodAsync('OnTabDropped', id, targetIndex);
    });
}

/** @param {HTMLElement} containerEl */
export function scrollTabIntoView(tabId) {
    const el = document.querySelector(`[data-tab-id="${tabId}"]`);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
}

// Translate vertical mouse-wheel events into horizontal scroll on the tab
// strip, so the user can scroll through tabs without needing a trackpad
// horizontal swipe.
/** @param {HTMLElement} containerEl */
export function initTabWheelScroll(containerEl) {
    if (!containerEl) return;
    containerEl.addEventListener('wheel', e => {
        if (e.deltaY === 0) return;
        e.preventDefault();
        containerEl.scrollLeft += e.deltaY * 0.8;
    }, { passive: false });
}
