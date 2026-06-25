// log-viewer-parser.js — ES module
// Pure logic: no DOM, fully testable with Vitest.
//
// Log format:  timestamp|level|host|pid|threadId|message|caller
//   • "message" may contain "|" — everything between the 5th and last "|" is the message
//   • Lines that do NOT start with a timestamp are continuations of the previous entry
//     (stack traces, exception detail lines, etc.)

/** @typedef {{ lineIndex:number, timestamp:string, level:string, host:string, pid:number, threadId:number, message:string, caller:string, continuations:string[], displayLineCount:number }} LogEntry */

// A log line starts with an ISO-like date: 2024-01-15 or 2024-01-15T
const TIMESTAMP_RE = /^\d{4}-\d{2}-\d{2}[T ]/;

export function isTimestampLine(line) {
    return TIMESTAMP_RE.test(line);
}

/**
 * Parse raw log text into an array of structured entries.
 * Multi-line continuation lines (no leading timestamp) are attached
 * to the preceding entry's `continuations` array.
 */
export function parseLog(rawText) {
    if (!rawText) return [];

    const lines = rawText.split('\n');
    const entries = [];
    let current = null;

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        // Skip a single trailing empty line (common at end of file)
        if (line === '' && i === lines.length - 1) continue;

        if (TIMESTAMP_RE.test(line)) {
            current = parseLine(line, i);
            entries.push(current);
        } else if (current !== null) {
            current.continuations.push(line);
            current.displayLineCount++;
        }
        // Lines before any entry (pre-header noise) are silently discarded
    }

    return entries;
}

function parseLine(line, lineIndex) {
    const parts = line.split('|');

    // timestamp=0, level=1, host=2, pid=3, threadId=4, message=5..n-2, caller=n-1
    // If fewer than 7 parts, be lenient and fill what we can.
    const timestamp = parts[0] ?? '';
    const level     = parts[1] ?? '';
    const host      = parts[2] ?? '';
    const pid       = parseInt(parts[3], 10) || 0;
    const threadId  = parts.length > 6 ? (parseInt(parts[4], 10) || 0) : 0;
    const caller    = parts.length > 6 ? (parts[parts.length - 1] ?? '') : '';
    const message   = parts.length > 6
        ? parts.slice(5, parts.length - 1).join('|')
        : (parts[5] ?? parts[4] ?? '');

    return { lineIndex, timestamp, level: level.trim(), host, pid, threadId, message, caller, continuations: [], displayLineCount: 1 };
}

// ── Search ──────────────────────────────────────────────────────────────────

/**
 * Build a search regex from a term and options.
 * Returns null if term is empty; throws on invalid regex.
 * @param {string} term
 * @param {{ isRegex?: boolean, caseSensitive?: boolean }} opts
 * @returns {RegExp | null}
 */
export function buildRegex(term, { isRegex = false, caseSensitive = false } = {}) {
    if (!term) return null;
    const flags = caseSensitive ? 'g' : 'gi';
    const pattern = isRegex ? term : escapeRegex(term);
    return new RegExp(pattern, flags); // throws SyntaxError on invalid regex
}

/**
 * Find all entry indices whose searchable text matches `re`.
 * Searches: level, host, message, caller, and all continuation lines.
 * @param {LogEntry[]} entries
 * @param {RegExp} re
 * @returns {number[]}
 */
export function findMatches(entries, re) {
    const results = [];
    for (let i = 0; i < entries.length; i++) {
        re.lastIndex = 0;
        if (re.test(entrySearchText(entries[i]))) results.push(i);
    }
    return results;
}

function entrySearchText(entry) {
    const base = `${entry.timestamp}|${entry.level}|${entry.host}|${entry.pid}|${entry.threadId}|${entry.message}|${entry.caller}`;
    return entry.continuations.length ? base + '\n' + entry.continuations.join('\n') : base;
}

// ── Highlight ────────────────────────────────────────────────────────────────

/**
 * Return an HTML string where every occurrence of `re` in `text` is
 * wrapped with <mark class="lv-hl">…</mark>.
 * Input text is HTML-escaped before matching.
 * @param {string} text
 * @param {RegExp | null} re
 * @returns {string}
 */
export function highlightText(text, re) {
    const safe = escapeHtml(text);
    if (!re) return safe;

    const cloned = new RegExp(re.source, re.flags.includes('g') ? re.flags : re.flags + 'g');
    cloned.lastIndex = 0;

    let result = '';
    let lastEnd = 0;
    let m;
    while ((m = cloned.exec(safe)) !== null) {
        result += safe.slice(lastEnd, m.index);
        result += `<mark class="lv-hl">${m[0]}</mark>`;
        lastEnd = cloned.lastIndex;
        if (m[0] === '') { cloned.lastIndex++; } // prevent infinite loop on zero-width match
    }
    result += safe.slice(lastEnd);
    return result;
}

// ── Utilities ────────────────────────────────────────────────────────────────

export function escapeHtml(text) {
    return String(text)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function escapeRegex(s) {
    return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
