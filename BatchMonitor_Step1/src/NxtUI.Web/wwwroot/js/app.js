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

/** @param {string} value — "dark" | "light" | "system" */
export function saveTheme(value) {
    try { localStorage.setItem('bm-theme', value); } catch { }
    document.cookie = `bm-theme=${value};path=/;max-age=31536000`;
}

/**
 * Registers a listener for OS dark-mode changes and returns the current OS value.
 * Saves the current OS preference as a cookie so the server can seed it on next load.
 * @param {{ invokeMethodAsync: (method: string, ...args: any[]) => Promise<void> }} dotnetRef
 * @returns {boolean} — true if OS is currently dark
 */
export function watchSystemTheme(dotnetRef) {
    var mq = window.matchMedia('(prefers-color-scheme: dark)');
    function persist(isDark) {
        document.cookie = 'bm-theme-system=' + (isDark ? 'dark' : 'light') + ';path=/;max-age=31536000';
    }
    mq.addEventListener('change', function (e) {
        persist(e.matches);
        dotnetRef.invokeMethodAsync('OnSystemThemeChanged', e.matches);
    });
    persist(mq.matches);
    return mq.matches;
}

/** Focuses the first focusable input inside a wrapper element after the next animation frame. @param {HTMLElement} wrapper */
export function focusAfterFrame(wrapper) {
    if (!wrapper) return;
    requestAnimationFrame(() => {
        const el = wrapper.querySelector('input, textarea') ?? wrapper;
        el.focus();
    });
}

/** Clicks the first input inside a wrapper element (used for hidden InputFile). @param {HTMLElement} wrapper */
export function clickInputInside(wrapper) {
    if (wrapper) wrapper.querySelector('input')?.click();
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

// ── Spark tooltip ─────────────────────────────────────────────────────────
// Pure JS — mousemove on Blazor Server costs a SignalR round-trip per pixel,
// so we handle it entirely client-side by reading the data-spark attribute.
(function () {
    // Abort previous registration (hot-reload safe) and remove stale tip divs.
    window._bmSparkCtrl?.abort();
    window._bmSparkCtrl = new AbortController();
    const signal = window._bmSparkCtrl.signal;
    document.querySelectorAll('#bm-spark-tip').forEach(el => el.remove());

    const tip = document.createElement('div');
    tip.id = 'bm-spark-tip';
    tip.style.cssText =
        'position:fixed;display:none;pointer-events:none;z-index:9999;' +
        'background:rgba(20,20,20,0.82);color:#e8e8e8;padding:4px 8px;' +
        'border-radius:4px;font-size:11px;white-space:pre;line-height:1.6;' +
        "font-family:'JetBrains Mono','Cascadia Code',Consolas,monospace;";
    document.body.appendChild(tip);

    function closestPt(data, target) {
        let best = data[0], bestDist = Math.abs(data[0][0] - target);
        for (const pt of data) {
            const d = Math.abs(pt[0] - target);
            if (d < bestDist) { bestDist = d; best = pt; }
        }
        return best;
    }

    function broadcastTimestamp(sourceCard, target) {
        document.querySelectorAll('.bm-svc-card').forEach(card => {
            if (card === sourceCard) return;
            const lbl = card.querySelector('.bm-svc-peak-label');
            if (!lbl) return;
            const svg = card.querySelector('.bm-svc-spark');
            if (!svg) return;
            let data;
            try { data = JSON.parse(svg.dataset.spark); } catch { return; }
            if (!data.length) return;
            if (!lbl.dataset.orig) lbl.dataset.orig = lbl.textContent;
            const pt = closestPt(data, target);
            lbl.textContent = Math.round(pt[1]).toLocaleString();
            lbl.style.opacity = '0.65';
            lbl.style.fontStyle = 'italic';
        });
    }

    function clearBroadcast(sourceCard) {
        document.querySelectorAll('.bm-svc-card').forEach(card => {
            if (card === sourceCard) return;
            const lbl = card.querySelector('.bm-svc-peak-label');
            if (lbl && lbl.dataset.orig !== undefined) {
                lbl.textContent = lbl.dataset.orig;
                lbl.style.opacity = '';
                lbl.style.fontStyle = '';
                delete lbl.dataset.orig;
            }
        });
    }

    document.addEventListener('mousemove', e => {
        const card = e.target.closest('.bm-svc-card');
        if (!card) { tip.style.display = 'none'; clearBroadcast(null); return; }
        const svg = card.querySelector('.bm-svc-spark');
        if (!svg) { tip.style.display = 'none'; clearBroadcast(card); return; }

        const raw = svg.dataset.spark;
        if (!raw) return;
        let data;
        try { data = JSON.parse(raw); } catch { return; }
        if (!data.length) return;

        // SVG is inset:0 on the card, so card bounds == SVG bounds
        const rect   = card.getBoundingClientRect();
        const frac   = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
        const t0     = data[0][0];
        const t1     = data[data.length - 1][0];
        const target = t0 + frac * (t1 - t0);

        const best = closestPt(data, target);

        const dt = new Date(best[0] * 1000);
        const hh = dt.getHours().toString().padStart(2, '0');
        const mm = dt.getMinutes().toString().padStart(2, '0');
        tip.textContent = `Time: ${hh}:${mm}\nMem:  ${Math.round(best[1]).toLocaleString()} MB\nPeak: ${Math.round(best[2]).toLocaleString()} MB`;
        tip.style.display = 'block';
        tip.style.left    = (e.clientX + 12) + 'px';
        tip.style.top     = (e.clientY - 28) + 'px';

        broadcastTimestamp(card, target);
    }, { signal });

    document.addEventListener('mouseleave', () => { tip.style.display = 'none'; clearBroadcast(null); }, { capture: true, signal });
})();

/** @param {HTMLElement} containerEl */
export function scrollTabIntoView(tabId) {
    const el = document.querySelector(`[data-tab-id="${tabId}"]`);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
}

// ── Inspector detail panel resizer (right side) ───────────────────────────
window.bmInspectorResizer = (function () {
    let dotnet = null, panelEl = null, startX = 0, startW = 0;

    function onMove(e) {
        const dx = e.clientX - startX;
        const w  = Math.max(250, Math.min(900, startW - dx));
        panelEl.style.width = w + 'px';
        dotnet.invokeMethodAsync('SetDetailWidth', w);
    }

    function onUp() {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup',   onUp);
        document.body.style.cursor     = '';
        document.body.style.userSelect = '';
    }

    return {
        start(clientX, dotnetRef, panel) {
            dotnet  = dotnetRef;
            panelEl = panel;
            startX  = clientX;
            startW  = panel.getBoundingClientRect().width;
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup',   onUp);
            document.body.style.cursor     = 'col-resize';
            document.body.style.userSelect = 'none';
        }
    };
})();

// ── Log Browser panel resizer ─────────────────────────────────────────────
window.logBrowserResizer = (function () {
    let dotnet = null, bodyEl = null, panelEl = null, startX = 0, startW = 0;

    function onMove(e) {
        const dx  = e.clientX - startX;
        const w   = Math.max(160, Math.min(600, startW + dx));
        panelEl.style.width = w + 'px';
        dotnet.invokeMethodAsync('SetTreeWidth', w);
    }

    function onUp() {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup',   onUp);
        document.body.style.cursor       = '';
        document.body.style.userSelect   = '';
    }

    return {
        start(clientX, dotnetRef, body, panel) {
            dotnet  = dotnetRef;
            bodyEl  = body;
            panelEl = panel;
            startX  = clientX;
            startW  = panel.getBoundingClientRect().width;
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup',   onUp);
            document.body.style.cursor     = 'col-resize';
            document.body.style.userSelect = 'none';
        }
    };
})();

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

// ── Client diagnostics monitor ────────────────────────────────────────────
// Measures JS event-loop lag and frame rate. Call bmClientDiag.start(dotnetRef)
// to begin; call bmClientDiag.stop() to end. dotnetRef must expose OnClientMetrics.
window.bmClientDiag = (function () {
    const TICK_MS  = 5000;   // how often to fire a setInterval ping
    const WARN_LAG = 200;    // ms — log if timer lag exceeds this

    let _dotnet  = null;
    let _timerId = null;
    let _rafId   = null;
    let _frameCount = 0;
    let _lastRafTs  = performance.now();
    let _lastTickTs = performance.now();

    function rafLoop(ts) {
        _frameCount++;
        _rafId = requestAnimationFrame(rafLoop);
    }

    function tick() {
        const now     = performance.now();
        const elapsed = now - _lastTickTs;
        const lag     = elapsed - TICK_MS;
        _lastTickTs   = now;

        const fps = _frameCount / (elapsed / 1000);
        _frameCount = 0;

        if (_dotnet) {
            _dotnet.invokeMethodAsync('OnClientMetrics', Math.round(lag), Math.round(fps));
        }
    }

    return {
        start(dotnetRef) {
            _dotnet     = dotnetRef;
            _frameCount = 0;
            _lastTickTs = performance.now();
            _rafId      = requestAnimationFrame(rafLoop);
            _timerId    = setInterval(tick, TICK_MS);
        },
        stop() {
            if (_timerId) { clearInterval(_timerId); _timerId = null; }
            if (_rafId)   { cancelAnimationFrame(_rafId); _rafId = null; }
            _dotnet = null;
        }
    };
})();
