// log-viewer.js — ES module, sets window.logViewer for Blazor JS interop.
// All rendering, virtual scroll, search, and bookmarks live here.
// The server only supplies raw text; parsing and DOM work stay in the browser.

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

// State keyed by container element so multiple viewers can coexist.
const _s = new WeakMap();

// ── Layout helpers ────────────────────────────────────────────────────────────

function entryH(entry) {
    return entry.displayLineCount * LINE_H + ENTRY_PAD;
}

/** Pre-compute cumulative Y positions. cum[i] = top of entry i. cum[n] = total height. */
function buildCumulatives(entries) {
    const cum = new Float64Array(entries.length + 1);
    for (let i = 0; i < entries.length; i++) cum[i + 1] = cum[i] + entryH(entries[i]);
    return cum;
}

/** Binary search: last entry whose cumulative start <= scrollTop. */
function findFirstVisible(cum, scrollTop) {
    let lo = 0, hi = cum.length - 2; // hi is last valid entry index
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

function renderRows(state) {
    const { entries, cum, matchSet, matchIndices, matchCursor, bookmarks, searchRe, scrollEl, rowsEl } = state;
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

        const bmBorder = bookmark ? `border-left:3px solid ${bookmark}` : 'border-left:3px solid transparent';
        const bmBg     = bookmark ? `background:${bookmark}18;` : '';
        buf.push(`<div class="${cls}" style="height:${h}px;${bmBorder};${bmBg}" data-i="${i}">`);
        buf.push(`<div class="lv-fields" style="white-space:${ws}">`);
        buf.push(`${bmHtml}`);
        buf.push(`<span class="lv-lineno" title="line ${e.lineIndex + 1}">${lineNo}</span>`);
        buf.push(`<span class="lv-ts">${escapeHtml(e.timestamp)}</span>`);
        buf.push(`<span class="lv-lv lv-lv-${levelCss}">${escapeHtml(e.level)}</span>`);
        buf.push(`<span class="lv-host">${escapeHtml(e.host)}</span>`);
        buf.push(`<span class="lv-pid">${escapeHtml(e.pid)}</span>`);
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
}

function updateInnerHeight(state) {
    state.innerEl.style.height = state.cum[state.entries.length] + 'px';
}

// ── Event handlers ────────────────────────────────────────────────────────────

function attachHandlers(container, state) {
    let rafId = null;
    state._scrollHandler = () => {
        if (rafId) return;
        rafId = requestAnimationFrame(() => { rafId = null; renderRows(state); });
    };
    state.scrollEl.addEventListener('scroll', state._scrollHandler, { passive: true });

    // Delegated click on bookmark markers
    state._clickHandler = (e) => {
        const bm = e.target.closest('.lv-bm');
        if (!bm) return;
        const row = bm.closest('.lv-row');
        if (!row) return;
        cycleBookmark(state, parseInt(row.dataset.i, 10));
        renderRows(state);
    };
    state.rowsEl.addEventListener('click', state._clickHandler);
}

function cycleBookmark(state, i) {
    const cur = state.bookmarks.get(i);
    if (!cur) {
        state.bookmarks.set(i, BOOKMARK_COLORS[0]);
    } else {
        const next = BOOKMARK_COLORS.indexOf(cur) + 1;
        if (next >= BOOKMARK_COLORS.length) state.bookmarks.delete(i);
        else state.bookmarks.set(i, BOOKMARK_COLORS[next]);
    }
}

// ── Scroll helpers ────────────────────────────────────────────────────────────

function scrollToEntry(state, idx) {
    if (idx < 0 || idx >= state.entries.length) return;
    state.scrollEl.scrollTop = state.cum[idx];
    renderRows(state);
}

function currentTopEntryIdx(state) {
    return findFirstVisible(state.cum, state.scrollEl.scrollTop);
}

// ── Public API ────────────────────────────────────────────────────────────────

/**
 * Render a log file into `container`.
 * @param {HTMLElement} container
 * @param {string}      rawText
 * @param {{ anchorLine?: number, fontSize?: number, wordWrap?: boolean }} options
 */
function render(container, rawText, options = {}) {
    destroy(container);

    const entries = parseLog(rawText ?? '');
    const cum     = buildCumulatives(entries);
    const { scrollEl, innerEl, rowsEl } = createDom(container, options);

    const state = {
        entries,
        cum,
        searchRe:     null,
        matchIndices: [],
        matchSet:     new Set(),
        matchCursor:  -1,
        bookmarks:    new Map(),
        scrollEl, innerEl, rowsEl,
        _scrollHandler: null,
        _clickHandler:  null,
    };
    _s.set(container, state);

    updateInnerHeight(state);
    attachHandlers(container, state);
    renderRows(state);

    if (options.anchorLine != null) scrollToLine(container, options.anchorLine);
}

/**
 * Append new raw text (live-tail delta). Preserves scroll position unless
 * the viewer was already at the bottom, in which case it follows the tail.
 */
function appendRaw(container, newRawText) {
    const state = _s.get(container);
    if (!state || !newRawText) return;

    const { scrollEl, cum, entries } = state;
    const atBottom = scrollEl.scrollTop + scrollEl.clientHeight >= cum[entries.length] - 10;

    const newEntries = parseLog(newRawText);
    if (newEntries.length === 0) return;

    // Fix lineIndex offsets: new entries' lineIndex values start from 0 of the delta text.
    // Offset them by the total source lines already loaded.
    const lastSourceLine = entries.length > 0
        ? entries[entries.length - 1].lineIndex + entries[entries.length - 1].displayLineCount
        : 0;
    for (const e of newEntries) {
        e.lineIndex += lastSourceLine;
        for (let i = 0; i < e.continuations.length; i++) {
            // continuations don't have their own lineIndex but displayLineCount already correct
        }
    }

    entries.push(...newEntries);
    state.cum = buildCumulatives(entries);

    if (state.searchRe) {
        const allMatches = findMatches(entries, state.searchRe);
        state.matchIndices = allMatches;
        state.matchSet     = new Set(allMatches);
        if (state.matchCursor >= allMatches.length) state.matchCursor = allMatches.length - 1;
    }

    updateInnerHeight(state);

    if (atBottom) scrollEl.scrollTop = state.cum[entries.length];

    renderRows(state);
}

/**
 * Run a search. Returns { count, current } for the Blazor toolbar to display.
 * @param {string} term
 * @param {{ isRegex?: boolean, caseSensitive?: boolean }} opts
 */
function find(container, term, opts = {}) {
    const state = _s.get(container);
    if (!state) return { count: 0, current: 0 };

    let re = null;
    try { re = buildRegex(term, opts); } catch { /* invalid regex — leave re null */ }

    state.searchRe    = re;
    state.matchCursor = -1;

    if (!re) {
        state.matchIndices = [];
        state.matchSet     = new Set();
        renderRows(state);
        return { count: 0, current: 0 };
    }

    const matches      = findMatches(state.entries, re);
    state.matchIndices = matches;
    state.matchSet     = new Set(matches);

    if (matches.length > 0) {
        state.matchCursor = 0;
        scrollToEntry(state, matches[0]);
    } else {
        renderRows(state);
    }

    return { count: matches.length, current: matches.length > 0 ? 1 : 0 };
}

function nextMatch(container) {
    const state = _s.get(container);
    if (!state || state.matchIndices.length === 0) return { count: 0, current: 0 };
    state.matchCursor = (state.matchCursor + 1) % state.matchIndices.length;
    scrollToEntry(state, state.matchIndices[state.matchCursor]);
    return { count: state.matchIndices.length, current: state.matchCursor + 1 };
}

function prevMatch(container) {
    const state = _s.get(container);
    if (!state || state.matchIndices.length === 0) return { count: 0, current: 0 };
    state.matchCursor = (state.matchCursor - 1 + state.matchIndices.length) % state.matchIndices.length;
    scrollToEntry(state, state.matchIndices[state.matchCursor]);
    return { count: state.matchIndices.length, current: state.matchCursor + 1 };
}

function nextBookmark(container) {
    const state = _s.get(container);
    if (!state || state.bookmarks.size === 0) return;
    const sorted = [...state.bookmarks.keys()].sort((a, b) => a - b);
    const top    = currentTopEntryIdx(state);
    scrollToEntry(state, sorted.find(i => i > top) ?? sorted[0]);
}

function prevBookmark(container) {
    const state = _s.get(container);
    if (!state || state.bookmarks.size === 0) return;
    const sorted = [...state.bookmarks.keys()].sort((a, b) => a - b);
    const top    = currentTopEntryIdx(state);
    scrollToEntry(state, [...sorted].reverse().find(i => i < top) ?? sorted[sorted.length - 1]);
}

/**
 * Scroll so that the entry whose `lineIndex` (source file line number) equals
 * `lineIndex` is at the top. Used for URL anchors (#L42) and log-browser links.
 */
function scrollToLine(container, lineIndex) {
    const state = _s.get(container);
    if (!state) return;
    const idx = state.entries.findIndex(e => e.lineIndex === lineIndex);
    if (idx >= 0) scrollToEntry(state, idx);
}

/** Returns the source line number of the top-most visible entry. Used for the share link. */
function getCurrentTopLine(container) {
    const state = _s.get(container);
    if (!state || state.entries.length === 0) return 0;
    return state.entries[currentTopEntryIdx(state)]?.lineIndex ?? 0;
}

function setFontSize(container, size) {
    const state = _s.get(container);
    if (!state) return;
    applyFontSize(state.scrollEl, size);
}

function setWordWrap(container, wrap) {
    const state = _s.get(container);
    if (!state) return;
    applyWordWrap(state.scrollEl, wrap);
    renderRows(state);
}

function destroy(container) {
    const state = _s.get(container);
    if (!state) return;
    if (state._scrollHandler) state.scrollEl.removeEventListener('scroll', state._scrollHandler);
    if (state._clickHandler)  state.rowsEl.removeEventListener('click',  state._clickHandler);
    container.innerHTML = '';
    _s.delete(container);
}

function renderLocalFile(container, key, opts) {
    const raw = window._localFiles?.get(key);
    if (raw == null) { console.error('[logViewer] local file key not found:', key); return; }
    render(container, raw, opts);
}

// Expose on window so Blazor JS interop (InvokeVoidAsync / InvokeAsync) can reach it.
window.logViewer = {
    render, renderLocalFile, appendRaw, find, nextMatch, prevMatch,
    nextBookmark, prevBookmark,
    scrollToLine, getCurrentTopLine,
    setFontSize, setWordWrap,
    destroy,
};
