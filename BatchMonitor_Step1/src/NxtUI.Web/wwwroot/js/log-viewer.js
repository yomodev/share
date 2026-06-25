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

import { parseLog, buildRegex, findMatches, highlightText, escapeHtml } from './log-viewer-parser.js';

// ── Constants ─────────────────────────────────────────────────────────────────

const LINE_H    = 20;  // px per display line within an entry
const ENTRY_PAD =  4;  // px total vertical padding per entry
const OVERSCAN  =  5;  // extra entries rendered above and below the visible window

const BOOKMARK_COLORS = ['#f6c90e', '#e06c75', '#61afef', '#98c379'];

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
    const { doc, cum, matchSet, matchIndices, matchCursor, searchRe, scrollEl, rowsEl } = vp;
    const { entries, bookmarks } = doc;
    const total = entries.length;

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
        const e = entries[i];
        const h = entryH(e);
        const isMatch   = matchSet.has(i);
        const isCurrent = i === currentMatchIdx;
        const bookmark  = bookmarks.get(i);
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
        buf.push(`<span class="lv-pid">${escapeHtml(e.pid)}</span>`);
        if (e.threadId) buf.push(`<span class="lv-tid">${escapeHtml(e.threadId)}</span>`);
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
            vp.cum = buildCumulatives(entries, vp.measuredH);
            updateInnerHeight(vp);
            rowsEl.style.top = vp.cum[first] + 'px';
        }
    }
}

function updateInnerHeight(vp) {
    vp.innerEl.style.height = vp.cum[vp.doc.entries.length] + 'px';
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

function cycleBookmark(vp, i) {
    const bm  = vp.doc.bookmarks;
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
    if (idx < 0 || idx >= vp.doc.entries.length) return;
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
        v.cum = buildCumulatives(entries, v.measuredH);

        if (v.searchRe) {
            const allMatches   = findMatches(entries, v.searchRe);
            v.matchIndices     = allMatches;
            v.matchSet         = new Set(allMatches);
            if (v.matchCursor >= allMatches.length) v.matchCursor = allMatches.length - 1;
        }

        updateInnerHeight(v);
        if (atBottom.get(v)) v.scrollEl.scrollTop = v.cum[entries.length];
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

    const matches      = findMatches(vp.doc.entries, re);
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
    const sorted = [...vp.doc.bookmarks.keys()].sort((a, b) => a - b);
    const top    = currentTopEntryIdx(vp);
    scrollToEntry(vp, sorted.find(i => i > top) ?? sorted[0]);
}

function prevBookmark(container) {
    const vp = _vp.get(container);
    if (!vp || vp.doc.bookmarks.size === 0) return;
    const sorted = [...vp.doc.bookmarks.keys()].sort((a, b) => a - b);
    const top    = currentTopEntryIdx(vp);
    scrollToEntry(vp, [...sorted].reverse().find(i => i < top) ?? sorted[sorted.length - 1]);
}

/**
 * Scroll so that the entry whose `lineIndex` equals `lineIndex` is at the top.
 */
function scrollToLine(container, lineIndex) {
    const vp = _vp.get(container);
    if (!vp) return;
    const idx = vp.doc.entries.findIndex(e => e.lineIndex === lineIndex);
    if (idx >= 0) scrollToEntry(vp, idx);
}

/** Returns the source line number of the top-most visible entry. */
function getCurrentTopLine(container) {
    const vp = _vp.get(container);
    if (!vp || vp.doc.entries.length === 0) return 0;
    return vp.doc.entries[currentTopEntryIdx(vp)]?.lineIndex ?? 0;
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
    vp.cum = buildCumulatives(vp.doc.entries);
    updateInnerHeight(vp);
    renderRows(vp);
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
    setFontSize, setWordWrap,
    destroy,
};
