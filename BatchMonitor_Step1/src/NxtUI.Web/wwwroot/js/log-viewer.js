// log-viewer.js — ES module, sets window.logViewer for Blazor JS interop.
// All rendering, virtual scroll, search, and bookmarks live here.
// The server only supplies raw text; parsing and DOM work stay in the browser.
//
// Architecture: Document / Viewport split
//   _docs  : Map<docId, Doc>        — one Doc per loaded file (shared entries + bookmarks)
//   _vp    : WeakMap<container, Vp> — one Vp per DOM container (own scroll/search/layout state)
// Multiple viewports can share one document (same entries, same bookmarks).
// appendRaw updates doc.entries and re-renders every attached viewport.
// destroy() ref-counts: when the last viewport is removed the doc is unloaded.

import { parseLog, buildRegex, findMatches, highlightText, escapeHtml } from './log-viewer-parser.js?v=4';
import { parse as parseFilter, evaluate as evalFilter } from './filter.js?v=2';

// ── Constants ─────────────────────────────────────────────────────────────────

const LINE_H    = 20;  // px per display line within an entry
const ENTRY_PAD =  4;  // px total vertical padding per entry
const OVERSCAN  =  5;  // extra entries rendered above and below the visible window

const BOOKMARK_COLORS = ['#f6c90e', '#e06c75', '#61afef', '#98c379'];

// Fields exposed to the filter engine. Bare terms (no field prefix) are matched
// against LOG_SEARCH_FIELDS. LOG_ALIASES resolve short names at parse time so the
// evaluated node always uses the canonical field name that exists on the entry object.
const LOG_SEARCH_FIELDS = ['level', 'host', 'pid', 'threadId', 'message', 'caller'];
const LOG_ALIASES = { lvl: 'level', msg: 'message', tid: 'threadId', ts: 'timestamp' };

const LEVEL_CSS = {
    ERROR: 'error', FATAL: 'error',
    WARN: 'warn', WARNING: 'warn',
    INFO: 'info',
    DEBUG: 'debug',
    TRACE: 'trace',
};

// ── State registries ──────────────────────────────────────────────────────────

const _docs = new Map();        // docId -> Doc
const _vp   = new WeakMap();    // container -> Vp

function _newDocId() {
    return crypto.randomUUID?.() ?? (Date.now().toString(36) + Math.random().toString(36));
}

// ── Layout helpers ────────────────────────────────────────────────────────────

function entryH(entry) {
    return entry.displayLineCount * LINE_H + ENTRY_PAD;
}

/** Pre-compute cumulative Y positions. cum[i] = top of entry i. cum[n] = total height.
 *  measuredH (optional Map<entryIndex, px>) overrides the estimated height for entries
 *  that have been rendered and measured (used when word-wrap is on). */
function buildCumulatives(entries, measuredH) {
    const cum = new Float64Array(entries.length + 1);
    for (let i = 0; i < entries.length; i++) {
        cum[i + 1] = cum[i] + (measuredH?.get(i) ?? entryH(entries[i]));
    }
    return cum;
}

// ── Filter helpers ────────────────────────────────────────────────────────────

/**
 * Rebuild vp.visibleEntries and vp.visibleDocIndices from doc.entries + vp.filterNode.
 * When no filter is active visibleEntries is the same array reference as doc.entries
 * (no copy) and visibleDocIndices is null (identity mapping).
 * Clears measuredH because visible indices are invalidated.
 */
function rebuildVisible(vp) {
    const { entries } = vp.doc;
    const node = vp.filterNode;
    if (node === null) {
        vp.visibleEntries    = entries;
        vp.visibleDocIndices = null;
    } else {
        const vis = [], idx = [];
        for (let i = 0; i < entries.length; i++) {
            if (evalFilter(node, entries[i])) { vis.push(entries[i]); idx.push(i); }
        }
        vp.visibleEntries    = vis;
        vp.visibleDocIndices = idx;
    }
    vp.measuredH.clear();
}

/** Map a visible-list index to the corresponding doc.entries index. */
function toDocIdx(vp, visIdx) {
    return vp.visibleDocIndices ? vp.visibleDocIndices[visIdx] : visIdx;
}

/** Binary search: last entry whose cumulative start <= scrollTop. */
function findFirstVisible(cum, scrollTop) {
    let lo = 0, hi = cum.length - 2;
    if (hi < 0) return 0;
    while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        if (cum[mid] <= scrollTop) lo = mid; else hi = mid - 1;
    }
    return lo;
}

// ── DOM construction ──────────────────────────────────────────────────────────

function createDom(container, opts) {
    container.innerHTML = '';
    container.style.cssText = 'position:relative;overflow:hidden;';

    const scrollEl = document.createElement('div');
    scrollEl.className = 'lv-scroll';
    scrollEl.style.cssText = 'position:absolute;inset:0;overflow-y:auto;overflow-x:auto;';
    container.appendChild(scrollEl);

    const innerEl = document.createElement('div');
    innerEl.style.cssText = 'position:relative;min-width:100%;';
    scrollEl.appendChild(innerEl);

    const rowsEl = document.createElement('div');
    rowsEl.style.cssText = 'position:absolute;top:0;left:0;right:0;';
    innerEl.appendChild(rowsEl);

    applyFontSize(scrollEl, opts.fontSize ?? 13);
    applyWordWrap(scrollEl, opts.wordWrap ?? false);

    return { scrollEl, innerEl, rowsEl };
}

function applyFontSize(scrollEl, size) {
    scrollEl.style.fontSize = size + 'px';
}

function applyWordWrap(scrollEl, wrap) {
    scrollEl.dataset.wrap = wrap ? '1' : '0';
}

// ── Row rendering ─────────────────────────────────────────────────────────────

function renderRows(vp) {
    const { doc, cum, matchSet, matchIndices, matchCursor, searchRe, scrollEl, rowsEl, visibleEntries } = vp;
    const { bookmarks } = doc;
    const total = visibleEntries.length;

    if (total === 0) {
        rowsEl.innerHTML = '<div class="lv-empty">No log entries.</div>';
        return;
    }

    const scrollTop  = scrollEl.scrollTop;
    const viewportH  = scrollEl.clientHeight || 400;
    const wordWrap   = scrollEl.dataset.wrap === '1';

    const firstRaw = findFirstVisible(cum, scrollTop);
    const first    = Math.max(0, firstRaw - OVERSCAN);
    let last = firstRaw;
    while (last < total && cum[last] < scrollTop + viewportH) last++;
    last = Math.min(total - 1, last + OVERSCAN);

    rowsEl.style.top = cum[first] + 'px';

    const currentMatchIdx = matchCursor >= 0 ? (matchIndices[matchCursor] ?? -1) : -1;
    const ws = wordWrap ? 'pre-wrap' : 'pre';
    const buf = [];

    for (let i = first; i <= last; i++) {
        const e = visibleEntries[i];
        const h = entryH(e);
        const isMatch   = matchSet.has(i);
        const isCurrent = i === currentMatchIdx;
        const bookmark  = bookmarks.get(toDocIdx(vp, i));
        const levelKey  = e.level.toUpperCase();
        const levelCss  = LEVEL_CSS[levelKey] || 'info';

        let cls = 'lv-row';
        if (isMatch)   cls += ' lv-match';
        if (isCurrent) cls += ' lv-match-current';
        if (bookmark)  cls += ' lv-bookmarked';

        const bmHtml = bookmark
            ? `<span class="lv-bm" style="background:${bookmark};color:#111" title="Bookmark (click to cycle)">▶</span>`
            : `<span class="lv-bm lv-bm-empty" title="Click to add bookmark"></span>`;

        const lineNo  = String(e.lineIndex + 1).padStart(5, ' ');
        const msgHtml = isMatch ? highlightText(e.message, searchRe) : escapeHtml(e.message);
        const calHtml = isMatch ? highlightText(e.caller,  searchRe) : escapeHtml(e.caller);

        const bmBorder   = bookmark ? `border-left:3px solid ${bookmark}` : 'border-left:3px solid transparent';
        const bmBg       = bookmark ? `background:${bookmark}2e;` : '';
        // When word-wrap is on, omit the fixed height so the row auto-sizes; heights
        // are measured after render and stored in vp.measuredH for virtual-scroll.
        const heightStyle = wordWrap ? '' : `height:${h}px;`;
        buf.push(`<div class="${cls}" style="${heightStyle}${bmBorder};${bmBg}" data-i="${i}">`);
        buf.push(`<div class="lv-fields" style="white-space:${ws}">`);
        buf.push(`${bmHtml}`);
        buf.push(`<span class="lv-lineno" title="line ${e.lineIndex + 1}">${lineNo}</span>`);
        const tsParts = e.timestamp.split(' ');
        const tsDate  = escapeHtml(tsParts[0] ?? e.timestamp);
        const tsTime  = tsParts[1] ? ` <span class="lv-ts-time">${escapeHtml(tsParts[1])}</span>` : '';
        buf.push(`<span class="lv-ts">${tsDate}${tsTime}</span>`);
        buf.push(`<span class="lv-lv lv-lv-${levelCss}">${escapeHtml(e.level)}</span>`);
        buf.push(`<span class="lv-host">${escapeHtml(e.host)}</span>`);
        buf.push(`<span class="lv-pid" title="pid">${escapeHtml(e.pid)}</span>`);
        if (e.threadId !== 0) buf.push(`<span class="lv-tid" title="tid">${escapeHtml(e.threadId)}</span>`);
        buf.push(`<span class="lv-msg">${msgHtml}</span>`);
        if (e.caller) buf.push(`<span class="lv-caller">${calHtml}</span>`);
        buf.push(`</div>`); // .lv-fields

        for (const cont of e.continuations) {
            const contHtml = isMatch ? highlightText(cont, searchRe) : escapeHtml(cont);
            buf.push(`<div class="lv-cont" style="white-space:${ws}">${contHtml}</div>`);
        }

        buf.push(`</div>`); // .lv-row
    }

    rowsEl.innerHTML = buf.join('');

    // After DOM is updated, measure actual row heights when word-wrap is on.
    // Virtual-scroll heights are estimated from displayLineCount and don't account
    // for text wrapping. Measuring and rebuilding cum fixes the overlap.
    if (wordWrap) {
        let changed = false;
        rowsEl.querySelectorAll('.lv-row[data-i]').forEach(row => {
            const i = parseInt(row.dataset.i);
            if (isNaN(i)) return;
            const h = row.offsetHeight;
            if (vp.measuredH.get(i) !== h) {
                vp.measuredH.set(i, h);
                changed = true;
            }
        });
        if (changed) {
            vp.cum = buildCumulatives(visibleEntries, vp.measuredH);
            updateInnerHeight(vp);
            rowsEl.style.top = vp.cum[first] + 'px';
        }
    }
}

function updateInnerHeight(vp) {
    vp.innerEl.style.height = vp.cum[vp.visibleEntries.length] + 'px';
}

// ── Event handlers ────────────────────────────────────────────────────────────

function attachHandlers(container, vp) {
    let rafId = null;
    vp._scrollHandler = () => {
        if (rafId) return;
        rafId = requestAnimationFrame(() => { rafId = null; renderRows(vp); });
    };
    vp.scrollEl.addEventListener('scroll', vp._scrollHandler, { passive: true });

    vp._clickHandler = (e) => {
        const bm = e.target.closest('.lv-bm');
        if (!bm) return;
        const row = bm.closest('.lv-row');
        if (!row) return;
        cycleBookmark(vp, parseInt(row.dataset.i, 10));
        renderRows(vp);
    };
    vp.rowsEl.addEventListener('click', vp._clickHandler);
}

function cycleBookmark(vp, visIdx) {
    const bm  = vp.doc.bookmarks;
    const i   = toDocIdx(vp, visIdx);
    const cur = bm.get(i);
    if (!cur) {
        bm.set(i, BOOKMARK_COLORS[0]);
    } else {
        const next = BOOKMARK_COLORS.indexOf(cur) + 1;
        if (next >= BOOKMARK_COLORS.length) bm.delete(i);
        else bm.set(i, BOOKMARK_COLORS[next]);
    }
}

// ── Scroll helpers ────────────────────────────────────────────────────────────

function scrollToEntry(vp, idx) {
    if (idx < 0 || idx >= vp.visibleEntries.length) return;
    vp.scrollEl.scrollTop = vp.cum[idx];
    renderRows(vp);
}

function currentTopEntryIdx(vp) {
    return findFirstVisible(vp.cum, vp.scrollEl.scrollTop);
}

// ── Viewport factory ──────────────────────────────────────────────────────────

function _createViewport(container, doc, options) {
    const { scrollEl, innerEl, rowsEl } = createDom(container, options);
    const vp = {
        doc,
        filterNode:       null,
        visibleEntries:   doc.entries,   // same reference when no filter active
        visibleDocIndices: null,         // null = identity mapping (no filter)
        cum:          buildCumulatives(doc.entries),
        measuredH:    new Map(),
        searchRe:     null,
        matchIndices: [],
        matchSet:     new Set(),
        matchCursor:  -1,
        scrollEl, innerEl, rowsEl,
        _scrollHandler: null,
        _clickHandler:  null,
    };
    doc._viewports.add(vp);
    _vp.set(container, vp);
    updateInnerHeight(vp);
    attachHandlers(container, vp);
    renderRows(vp);
    return vp;
}

// ── Public API ────────────────────────────────────────────────────────────────

/**
 * Render a log file into `container`. Creates a new document and a viewport.
 * Returns the docId so callers can attach additional viewports later.
 * @param {HTMLElement} container
 * @param {string}      rawText
 * @param {{ anchorLine?: number, fontSize?: number, wordWrap?: boolean }} options
 * @returns {string} docId
 */
function render(container, rawText, options = {}) {
    destroy(container);

    const docId = _newDocId();
    const doc = {
        docId,
        entries:    parseLog(rawText ?? ''),
        bookmarks:  new Map(),
        _viewports: new Set(),
    };
    _docs.set(docId, doc);

    const vp = _createViewport(container, doc, options);

    if (options.anchorLine != null) scrollToLine(container, options.anchorLine);

    return docId;
}

/**
 * Attach an additional viewport to an existing document.
 * Use the docId returned by render() or getDocId().
 * @param {HTMLElement} container
 * @param {string}      docId
 * @param {{ anchorLine?: number, fontSize?: number, wordWrap?: boolean }} options
 */
function addViewport(container, docId, options = {}) {
    destroy(container);
    const doc = _docs.get(docId);
    if (!doc) { console.error('[logViewer] addViewport: doc not found:', docId); return; }
    const vp = _createViewport(container, doc, options);
    if (options.anchorLine != null) scrollToLine(container, options.anchorLine);
}

/** Returns the docId for the document currently shown in container. */
function getDocId(container) {
    return _vp.get(container)?.doc.docId ?? null;
}

/**
 * Append new raw text (live-tail delta). Updates the shared document and
 * re-renders every viewport showing it.
 */
function appendRaw(container, newRawText) {
    const vp = _vp.get(container);
    if (!vp || !newRawText) return;

    const doc     = vp.doc;
    const entries = doc.entries;

    const newEntries = parseLog(newRawText);
    if (newEntries.length === 0) return;

    const lastSourceLine = entries.length > 0
        ? entries[entries.length - 1].lineIndex + entries[entries.length - 1].displayLineCount
        : 0;
    for (const e of newEntries) e.lineIndex += lastSourceLine;

    // Snapshot atBottom for each viewport BEFORE mutating entries/cum.
    const atBottom = new Map();
    for (const v of doc._viewports) {
        atBottom.set(v, v.scrollEl.scrollTop + v.scrollEl.clientHeight >= v.cum[entries.length] - 10);
    }

    entries.push(...newEntries);

    for (const v of doc._viewports) {
        rebuildVisible(v);
        v.cum = buildCumulatives(v.visibleEntries, v.measuredH);

        if (v.searchRe) {
            const allMatches   = findMatches(v.visibleEntries, v.searchRe);
            v.matchIndices     = allMatches;
            v.matchSet         = new Set(allMatches);
            if (v.matchCursor >= allMatches.length) v.matchCursor = allMatches.length - 1;
        }

        updateInnerHeight(v);
        if (atBottom.get(v)) v.scrollEl.scrollTop = v.cum[v.visibleEntries.length];
        renderRows(v);
    }
}

/**
 * Run a search. Returns { count, current } for the Blazor toolbar to display.
 * @param {string} term
 * @param {{ isRegex?: boolean, caseSensitive?: boolean }} opts
 */
function find(container, term, opts = {}) {
    const vp = _vp.get(container);
    if (!vp) return { count: 0, current: 0 };

    let re = null;
    try { re = buildRegex(term, opts); } catch { /* invalid regex — leave re null */ }

    vp.searchRe    = re;
    vp.matchCursor = -1;

    if (!re) {
        vp.matchIndices = [];
        vp.matchSet     = new Set();
        renderRows(vp);
        return { count: 0, current: 0 };
    }

    const matches      = findMatches(vp.visibleEntries, re);
    vp.matchIndices    = matches;
    vp.matchSet        = new Set(matches);

    if (matches.length > 0) {
        vp.matchCursor = 0;
        scrollToEntry(vp, matches[0]);
    } else {
        renderRows(vp);
    }

    return { count: matches.length, current: matches.length > 0 ? 1 : 0 };
}

function nextMatch(container) {
    const vp = _vp.get(container);
    if (!vp || vp.matchIndices.length === 0) return { count: 0, current: 0 };
    vp.matchCursor = (vp.matchCursor + 1) % vp.matchIndices.length;
    scrollToEntry(vp, vp.matchIndices[vp.matchCursor]);
    return { count: vp.matchIndices.length, current: vp.matchCursor + 1 };
}

function prevMatch(container) {
    const vp = _vp.get(container);
    if (!vp || vp.matchIndices.length === 0) return { count: 0, current: 0 };
    vp.matchCursor = (vp.matchCursor - 1 + vp.matchIndices.length) % vp.matchIndices.length;
    scrollToEntry(vp, vp.matchIndices[vp.matchCursor]);
    return { count: vp.matchIndices.length, current: vp.matchCursor + 1 };
}

function nextBookmark(container) {
    const vp = _vp.get(container);
    if (!vp || vp.doc.bookmarks.size === 0) return;
    // Collect visible indices that have bookmarks, in order.
    const visMarked = [];
    for (let i = 0; i < vp.visibleEntries.length; i++) {
        if (vp.doc.bookmarks.has(toDocIdx(vp, i))) visMarked.push(i);
    }
    if (visMarked.length === 0) return;
    const top = currentTopEntryIdx(vp);
    scrollToEntry(vp, visMarked.find(i => i > top) ?? visMarked[0]);
}

function prevBookmark(container) {
    const vp = _vp.get(container);
    if (!vp || vp.doc.bookmarks.size === 0) return;
    const visMarked = [];
    for (let i = 0; i < vp.visibleEntries.length; i++) {
        if (vp.doc.bookmarks.has(toDocIdx(vp, i))) visMarked.push(i);
    }
    if (visMarked.length === 0) return;
    const top = currentTopEntryIdx(vp);
    scrollToEntry(vp, [...visMarked].reverse().find(i => i < top) ?? visMarked[visMarked.length - 1]);
}

/**
 * Scroll so that the entry whose `lineIndex` equals `lineIndex` is at the top.
 */
function scrollToLine(container, lineIndex) {
    const vp = _vp.get(container);
    if (!vp) return;
    const idx = vp.visibleEntries.findIndex(e => e.lineIndex === lineIndex);
    if (idx >= 0) scrollToEntry(vp, idx);
}

/** Returns the source line number of the top-most visible entry. */
function getCurrentTopLine(container) {
    const vp = _vp.get(container);
    if (!vp || vp.visibleEntries.length === 0) return 0;
    return vp.visibleEntries[currentTopEntryIdx(vp)]?.lineIndex ?? 0;
}

function setFontSize(container, size) {
    const vp = _vp.get(container);
    if (!vp) return;
    applyFontSize(vp.scrollEl, size);
}

function setWordWrap(container, wrap) {
    const vp = _vp.get(container);
    if (!vp) return;
    applyWordWrap(vp.scrollEl, wrap);
    vp.measuredH.clear();
    vp.cum = buildCumulatives(vp.visibleEntries);
    updateInnerHeight(vp);
    renderRows(vp);
}

/**
 * Apply a filter expression to the viewport. Only entries matching the filter
 * are shown; search operates on the filtered set.
 * Pass an empty string or null to clear the filter and show all entries.
 * Returns { total, visible } so Blazor can update a counter in the toolbar.
 */
function setFilter(container, filterStr) {
    console.log('[filter] setFilter called, expr:', filterStr);
    const vp = _vp.get(container);
    if (!vp) { console.error('[filter] no viewport found for container'); return { total: 0, visible: 0 }; }

    let node = null;
    try { node = filterStr ? parseFilter(filterStr, LOG_SEARCH_FIELDS, LOG_ALIASES) : null; }
    catch (e) { console.error('[filter] parse error:', e); }

    if (filterStr) {
        const sample = vp.doc.entries[0];
        console.log('[filter] expr:', filterStr,
            '| node:', JSON.stringify(node),
            '| sample pid:', sample?.pid, '(type:', typeof sample?.pid + ')',
            '| sample threadId:', sample?.threadId, '(type:', typeof sample?.threadId + ')');
    }

    vp.filterNode = node;
    rebuildVisible(vp);
    vp.cum = buildCumulatives(vp.visibleEntries, vp.measuredH);

    console.log('[filter] result: visible', vp.visibleEntries.length, '/', vp.doc.entries.length);

    // Re-run search over the new visible set.
    if (vp.searchRe) {
        const matches      = findMatches(vp.visibleEntries, vp.searchRe);
        vp.matchIndices    = matches;
        vp.matchSet        = new Set(matches);
        vp.matchCursor     = matches.length > 0 ? 0 : -1;
    }

    updateInnerHeight(vp);
    renderRows(vp);

    return { total: vp.doc.entries.length, visible: vp.visibleEntries.length };
}

/**
 * Remove a viewport. If it was the last viewport for its document,
 * the document is unloaded from memory.
 */
function destroy(container) {
    const vp = _vp.get(container);
    if (!vp) return;

    if (vp._scrollHandler) vp.scrollEl.removeEventListener('scroll', vp._scrollHandler);
    if (vp._clickHandler)  vp.rowsEl.removeEventListener('click',  vp._clickHandler);
    container.innerHTML = '';

    const doc = vp.doc;
    doc._viewports.delete(vp);
    if (doc._viewports.size === 0) _docs.delete(doc.docId);

    _vp.delete(container);
}

function renderLocalFile(container, key, opts) {
    const raw = window._localFiles?.get(key);
    if (raw == null) { console.error('[logViewer] local file key not found:', key); return; }
    render(container, raw, opts);
}

// Expose on window so Blazor JS interop (InvokeVoidAsync / InvokeAsync) can reach it.
window.logViewer = {
    render, renderLocalFile, addViewport, getDocId,
    appendRaw, find, nextMatch, prevMatch,
    nextBookmark, prevBookmark,
    scrollToLine, getCurrentTopLine,
    setFontSize, setWordWrap, setFilter,
    destroy,
};
