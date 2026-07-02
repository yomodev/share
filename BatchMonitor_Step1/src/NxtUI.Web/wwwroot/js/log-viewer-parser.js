// log-viewer-parser.js — ES module
// Pure logic: no DOM, fully testable with Vitest.
//
// Supports two parsing modes:
//   1. Legacy (no formats supplied): fixed-position pipe-delimited format
//      timestamp|level|host|pid|threadId|message|caller[|extra1|...]
//   2. Configurable (formats array supplied): each format string uses placeholders
//      {timestamp}, {level}, {host}, {pid}, {thread}, {message}, {caller},
//      {*} (anything on the line, non-capturing), {$} (end of line), {newline}
//      The best matching format is auto-detected from the first lines.
//      If no format matches, every line is treated as plain text.

/** @typedef {{ lineIndex:number, timestamp:string, level:string, host:string, pid:number, threadId:number, message:string, caller:string, extras:string[], continuations:string[], displayLineCount:number, isPlainText?:boolean }} LogEntry */

// A log line starts with an ISO-like date: 2024-01-15 or 2024-01-15T
const TIMESTAMP_RE = /^\d{4}-\d{2}-\d{2}[T ]/;

export function isTimestampLine(line) {
    return TIMESTAMP_RE.test(line);
}

// ── Configurable format compiler ─────────────────────────────────────────────

/**
 * Compile a placeholder format string into a named-capture regex.
 * Returns a compiled format object, or null if the string is invalid/trivial.
 * @param {string} formatStr
 * @returns {{ regex: RegExp, fieldNames: string[], hasNewline: boolean, formatStr: string } | null}
 */
export function compileFormat(formatStr) {
    if (!formatStr) return null;

    // Tokenize: split into placeholders and literals
    const tokens = [];
    const tokenRe = /\{([^}]+)\}/g;
    let last = 0, m;
    while ((m = tokenRe.exec(formatStr)) !== null) {
        if (m.index > last) tokens.push({ type: 'lit', val: formatStr.slice(last, m.index) });
        tokens.push({ type: 'ph', name: m[1] });
        last = m.index + m[0].length;
    }
    if (last < formatStr.length) tokens.push({ type: 'lit', val: formatStr.slice(last) });

    const CAPTURE = new Set(['timestamp', 'level', 'host', 'pid', 'thread', 'message', 'caller']);
    const fieldNames = [];
    let pattern = '^';
    let hasNewline = false;

    for (let i = 0; i < tokens.length; i++) {
        const tok = tokens[i];
        if (tok.type === 'lit') {
            pattern += escapeRegex(tok.val);
            continue;
        }
        const name = tok.name;
        if (name === 'newline') { pattern += '\\n'; hasNewline = true; continue; }
        if (name === '$')       { pattern += '(?=$|\\n)'; continue; }
        if (name === '*')       { pattern += '[^\\n]*'; continue; }

        // Determine stopper based on what follows this field
        const next = tokens[i + 1];
        let stopper;
        if (!next) {
            stopper = '[^\\n]*';  // last token: greedy to EOL
        } else if (next.type === 'ph') {
            if (next.name === '$' || next.name === '*' || next.name === 'newline') {
                stopper = '[^\\n]*';  // before EOL marker: greedy
            } else {
                stopper = '[^\\n]*?'; // before another capture: non-greedy
            }
        } else {
            // Next is a literal — stop before its first character
            const fc = next.val[0];
            const fcEsc = (fc === ']' || fc === '\\' || fc === '^' || fc === '-') ? ('\\' + fc) : fc;
            stopper = `[^\\n${fcEsc}]*`;
        }

        if (CAPTURE.has(name)) {
            pattern += `(?<${name}>${stopper})`;
            fieldNames.push(name);
        } else {
            pattern += `(?:${stopper})`;
        }
    }

    if (fieldNames.length < 2) return null; // too few fields to be useful

    return { regex: new RegExp(pattern, 'gm'), fieldNames, hasNewline, formatStr };
}

/**
 * Detect the best matching format for rawText by trying each compiled format.
 * Returns the first format that matches at least 3 times in the first 10 KB,
 * or null (plain text fallback).
 * @param {string} rawText
 * @param {Array} compiledFormats
 * @returns {object|null}
 */
export function detectFormat(rawText, compiledFormats) {
    if (!compiledFormats || compiledFormats.length === 0) return null;
    const sample = rawText.length > 10000 ? rawText.slice(0, 10000) : rawText;
    for (const fmt of compiledFormats) {
        const re = new RegExp(fmt.regex.source, fmt.regex.flags);
        re.lastIndex = 0;
        let matches = 0;
        let hit;
        while ((hit = re.exec(sample)) !== null) {
            if (hit[0] === '') { re.lastIndex++; continue; }
            if (++matches >= 3) return fmt;
        }
    }
    return null;
}

// ── Format-driven parsing ────────────────────────────────────────────────────

/**
 * Parse rawText using a pre-detected format (skips detection).
 * Pass detectedFormat = null for plain-text mode.
 * Used by appendRaw to continue with the same format as the initial render.
 * @param {string} rawText
 * @param {object|null} detectedFormat
 * @returns {LogEntry[]}
 */
export function parseMore(rawText, detectedFormat) {
    if (!rawText) return [];
    return detectedFormat ? parseWithFormat(rawText, detectedFormat) : parsePlainText(rawText);
}

function parsePlainText(rawText) {
    const lines = rawText.split('\n');
    const entries = [];
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        if (line === '' && i === lines.length - 1) continue;
        entries.push({
            lineIndex: i, timestamp: '', level: '', host: '',
            pid: 0, threadId: 0, message: line, caller: '',
            extras: [], continuations: [], displayLineCount: 1,
            isPlainText: true,
        });
    }
    return entries;
}

function parseWithFormat(rawText, fmt) {
    const re = new RegExp(fmt.regex.source, fmt.regex.flags);
    re.lastIndex = 0;
    const entries = [];
    let lineIndex = 0;
    let prevEnd   = 0;
    let m;

    while ((m = re.exec(rawText)) !== null) {
        if (m[0] === '') { re.lastIndex++; continue; }

        const g = m.groups ?? {};

        // Count newlines in gap between previous match end and this match start
        const gap = rawText.slice(prevEnd, m.index);
        const gapNewlines = countNewlines(gap);
        lineIndex += gapNewlines;

        // Gap content (continuation lines of the previous entry)
        if (gap.trim() && entries.length > 0) {
            const contLines = gap.split('\n').filter(l => l.length > 0);
            const last = entries[entries.length - 1];
            last.continuations.push(...contLines);
            last.displayLineCount += contLines.length;
        }

        const matchNewlines = countNewlines(m[0]);

        entries.push({
            lineIndex,
            timestamp: (g.timestamp ?? '').trim(),
            level:     (g.level     ?? '').trim(),
            host:      g.host    ?? '',
            pid:       parseInt(g.pid,    10) || 0,
            threadId:  parseInt(g.thread, 10) || 0,
            message:   g.message ?? '',
            caller:    g.caller  ?? '',
            extras:    [],
            continuations:    [],
            displayLineCount: 1 + matchNewlines,
            isPlainText:      false,
        });

        lineIndex += matchNewlines;
        prevEnd    = re.lastIndex;
    }

    // Remaining text after last match → continuation of last entry
    if (prevEnd < rawText.length && entries.length > 0) {
        const tail = rawText.slice(prevEnd);
        const tailLines = tail.split('\n').filter(l => l.length > 0);
        const last = entries[entries.length - 1];
        last.continuations.push(...tailLines);
        last.displayLineCount += tailLines.length;
    }

    return entries;
}

function countNewlines(text) {
    let n = 0;
    for (let i = 0; i < text.length; i++) {
        if (text[i] === '\n') n++;
    }
    return n;
}

/**
 * Parse raw log text into an array of structured entries.
 * Auto-detects the best matching format from compiledFormats and falls back
 * to plain text if none matches (every line becomes its own entry).
 * @param {string} rawText
 * @param {Array} [compiledFormats]
 * @returns {LogEntry[]}
 */
export function parseLog(rawText, compiledFormats) {
    if (!rawText) return [];
    const fmt = detectFormat(rawText, compiledFormats ?? []);
    return fmt ? parseWithFormat(rawText, fmt) : parsePlainText(rawText);
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
    return new RegExp(pattern, flags);
}

/**
 * Find all entry indices whose searchable text matches `re`.
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
    if (entry.isPlainText) {
        const base = entry.message;
        return entry.continuations.length ? base + '\n' + entry.continuations.join('\n') : base;
    }
    const base = `${entry.timestamp}|${entry.level}|${entry.host}|${entry.pid}|${entry.threadId}|${entry.message}|${entry.caller}`
        + (entry.extras.length ? '|' + entry.extras.join('|') : '');
    return entry.continuations.length ? base + '\n' + entry.continuations.join('\n') : base;
}

// ── Highlight ────────────────────────────────────────────────────────────────

/**
 * Return an HTML string where every occurrence of `re` in `text` is
 * wrapped with <mark class="lv-hl">…</mark>.
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
        if (m[0] === '') { cloned.lastIndex++; }
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
