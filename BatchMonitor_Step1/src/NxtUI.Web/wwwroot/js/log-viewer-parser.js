// log-viewer-parser.js — ES module
// Pure logic: no DOM, fully testable with Vitest.
//
// Log lines are matched against the app's configured Logs:Formats templates (see
// appsettings.json for the placeholder reference — {timestamp}, {level}, {host}, {pid},
// {thread}, {message}, {caller}, {*}, {$}, {newline}). compileMultiFormat() combines every
// configured template into ONE regex (each an alternative), and parseMultiFormat() tries
// them all at every match position — see compileMultiFormat's own doc for why a single
// "best" format for the whole file is the wrong model (a real file can genuinely mix line
// shapes, e.g. most lines omit {host} but a few don't). No formats configured, or a line
// matching none of them, falls back to plain text (every line becomes its own entry, no
// fields parsed) — see parsePlainText.
//
// This mirrors LogBrowserService.ParseEntry (C#, used by the Log Browser's server-side
// search) — see log-format-grammar.contract.test.js for the shared grammar contract
// between compileFormat() here and LogBrowserService.CompileFormat.

/** @typedef {{ lineIndex:number, timestamp:string, level:string, host:string, pid:number, threadId:number, message:string, caller:string, extras:string[], continuations:string[], displayLineCount:number, isPlainText?:boolean }} LogEntry */

// A log line starts with an ISO-like date: 2024-01-15 or 2024-01-15T
const TIMESTAMP_RE = /^\d{4}-\d{2}-\d{2}[T ]/;

// Compiled formats' {newline} token only matches a bare "\n" — a literal token placed
// immediately before it (e.g. a lone "." line) would otherwise fail to match on a
// Windows-authored (CRLF) log file, since the "\r" sits between the literal and the
// "\n" the pattern expects. Normalizing once at every text-ingestion point keeps line
// counting ({newline} matches, lineIndex, split('\n')) consistent everywhere else too.
function normalizeNewlines(text) {
    return text.indexOf('\r') === -1 ? text : text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
}

export function isTimestampLine(line) {
    return TIMESTAMP_RE.test(line);
}

// ── Configurable format compiler ─────────────────────────────────────────────

const CAPTURE_FIELDS = new Set(['timestamp', 'level', 'host', 'pid', 'thread', 'message', 'caller']);

// Shared tokenizer + pattern builder behind both compileFormat (one format, used alone)
// and compileMultiFormat (several formats combined into one alternation — see there for
// why that exists). `groupPrefix` namespaces this format's named capture groups (e.g.
// "f1_host" instead of "host") so multiple formats' patterns can coexist as alternatives
// in a single regex without a duplicate-group-name error — JS does allow the same name to
// repeat across mutually-exclusive alternation branches on newer engines, but that's a
// recent (~2023) addition, not safe to rely on for portability, so this sidesteps it
// entirely with unique names plus a lookup table mapping them back to the logical field.
// Returns null if the format string captures fewer than 2 fields (too few to be useful).
function buildPatternBody(formatStr, groupPrefix) {
    if (!formatStr) return null;

    const tokens = [];
    const tokenRe = /\{([^}]+)\}/g;
    let last = 0, m;
    while ((m = tokenRe.exec(formatStr)) !== null) {
        if (m.index > last) tokens.push({ type: 'lit', val: formatStr.slice(last, m.index) });
        tokens.push({ type: 'ph', name: m[1] });
        last = m.index + m[0].length;
    }
    if (last < formatStr.length) tokens.push({ type: 'lit', val: formatStr.slice(last) });

    const fieldNames = [];
    let body = '';
    let hasNewline = false;

    for (let i = 0; i < tokens.length; i++) {
        const tok = tokens[i];
        if (tok.type === 'lit') {
            body += escapeRegex(tok.val);
            continue;
        }
        const name = tok.name;
        if (name === 'newline') { body += '\\n'; hasNewline = true; continue; }
        if (name === '$')       { body += '(?=$|\\n)'; continue; }
        if (name === '*')       { body += '[^\\n]*'; continue; }

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

        if (CAPTURE_FIELDS.has(name)) {
            body += `(?<${groupPrefix}${name}>${stopper})`;
            fieldNames.push(name);
        } else {
            body += `(?:${stopper})`;
        }
    }

    if (fieldNames.length < 2) return null; // too few fields to be useful
    return { body, fieldNames, hasNewline };
}

/**
 * Compile a placeholder format string into a named-capture regex.
 * Returns a compiled format object, or null if the string is invalid/trivial.
 * @param {string} formatStr
 * @returns {{ regex: RegExp, fieldNames: string[], hasNewline: boolean, formatStr: string } | null}
 */
export function compileFormat(formatStr) {
    const built = buildPatternBody(formatStr, '');
    if (!built) return null;
    return { regex: new RegExp('^' + built.body, 'gm'), fieldNames: built.fieldNames, hasNewline: built.hasNewline, formatStr };
}

/**
 * Compiles SEVERAL format strings into ONE combined regex (each format an alternative,
 * tried at every position — see parseMultiFormat) instead of picking a single "winning"
 * format for the whole file the way detectFormat/compileFormat+parseMore does. This is
 * what actually handles a file whose lines are a genuine MIX of shapes (e.g. most lines
 * omit {host}, a handful include it) correctly: each line is matched against whichever
 * configured format actually fits IT, rather than the whole file being forced through one
 * globally-chosen template that necessarily mis-parses (or fails to match at all) every
 * line of the "wrong" shape. Formats are tried in the order given — first (in the array)
 * alternative that matches at a given position wins there, same "first configured template
 * wins" precedence documented in appsettings.json's Logs:Formats comment; a caller wanting
 * "more descriptive shapes preferred" should order its formats with more fields first, as
 * the shipped default Logs:Formats already does.
 * @param {string[]} formatStrs
 * @returns {{ regex: RegExp, alternatives: {prefix:string, fieldNames:string[], hasNewline:boolean, formatStr:string}[] } | null}
 */
export function compileMultiFormat(formatStrs) {
    if (!formatStrs || formatStrs.length === 0) return null;
    const alternatives = [];
    const bodies = [];
    formatStrs.forEach((formatStr, i) => {
        const prefix = `f${i}_`;
        const built = buildPatternBody(formatStr, prefix);
        if (!built) return; // invalid/trivial format — skip, don't fail the whole batch
        alternatives.push({ prefix, fieldNames: built.fieldNames, hasNewline: built.hasNewline, formatStr });
        bodies.push(`(?:${built.body})`);
    });
    if (bodies.length === 0) return null;
    const regex = new RegExp('^(?:' + bodies.join('|') + ')', 'gm');
    return { regex, alternatives };
}

/**
 * Detect the best matching format for rawText by trying each compiled format.
 *
 * A configured format list is a fallback CHAIN in principle (docs/appsettings.json:
 * "the first template whose pattern matches ... wins"), but this function still has
 * to pick exactly ONE format for the whole file/sample — a single log file is not
 * re-detected line by line. Selection therefore works in two tiers:
 *   1. Group every format that matched at least twice (repetition, not a one-off
 *      accidental match) by how many fields it captures, and take the group with the
 *      MOST fields — a more descriptive format (e.g. one with {host}) should win over
 *      a less descriptive one (missing {host}) whenever the more descriptive shape is
 *      genuinely present more than once, even if it's a MINORITY of the sample (a
 *      file where most lines omit host but a few include it is real and common — the
 *      lines that DO have it deserve to be parsed correctly, not treated as noise).
 *   2. If nothing reaches that "at least twice" bar (e.g. a short errors.log with a
 *      single entry), fall back to whichever single format matched at all, preferring
 *      more fields then more matched length, same as the tie-break below.
 * Total matched character count is only ever a TIE-BREAK within an already-chosen
 * field-count tier — never a way to let a less descriptive format outrank a more
 * descriptive one just because it happened to match more (shorter) lines.
 * @param {string} rawText
 * @param {Array} compiledFormats
 * @returns {object|null}
 */
export function detectFormat(rawText, compiledFormats) {
    if (!compiledFormats || compiledFormats.length === 0) return null;
    rawText = normalizeNewlines(rawText);
    const sample = rawText.length > 10000 ? rawText.slice(0, 10000) : rawText;

    const scored = [];
    for (const fmt of compiledFormats) {
        const re = new RegExp(fmt.regex.source, fmt.regex.flags);
        re.lastIndex = 0;
        let matches = 0, totalLen = 0;
        let hit;
        while ((hit = re.exec(sample)) !== null) {
            if (hit[0] === '') { re.lastIndex++; continue; }
            matches++;
            totalLen += hit[0].length;
        }
        if (matches === 0) continue;
        scored.push({ fmt, matches, totalLen });
    }
    if (scored.length === 0) return null;

    const repeated = scored.filter(s => s.matches >= 2);
    const pool = repeated.length > 0 ? repeated : scored;
    pool.sort((a, b) => b.fmt.fieldNames.length - a.fmt.fieldNames.length || b.totalLen - a.totalLen);
    return pool[0].fmt;
}

// ── Format-driven parsing ────────────────────────────────────────────────────

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

/**
 * Parses rawText against a compileMultiFormat() result — every configured format is
 * tried at each match position, and whichever alternative actually fired (JS leaves a
 * non-participating alternation branch's named groups `undefined`, which is exactly the
 * signal used to tell them apart) supplies that entry's fields. This is what makes a file
 * with genuinely mixed line shapes (see compileMultiFormat's own doc) parse correctly:
 * a "no host" line and a "with host" line elsewhere in the SAME file each get read by
 * whichever format actually describes them, instead of the whole file being forced
 * through one globally "best" template that necessarily gets every line of the other
 * shape wrong (or matches it at all).
 * @param {string} rawText
 * @param {object|null} multiCompiled compileMultiFormat() result; null → plain-text mode
 * @returns {LogEntry[]}
 */
export function parseMultiFormat(rawText, multiCompiled) {
    if (!rawText) return [];
    rawText = normalizeNewlines(rawText);
    if (!multiCompiled) return parsePlainText(rawText);

    const { regex, alternatives } = multiCompiled;
    const re = new RegExp(regex.source, regex.flags);
    re.lastIndex = 0;
    const entries = [];
    let lineIndex = 0;
    let prevEnd = 0;
    let m;

    while ((m = re.exec(rawText)) !== null) {
        if (m[0] === '') { re.lastIndex++; continue; }

        const g = m.groups ?? {};
        // Exactly one alternative's groups are non-undefined per match (regex alternation
        // semantics) — find it by checking its own first field, which is always present
        // whenever that branch is the one that fired (buildPatternBody requires >= 2
        // captured fields per format, so there's always at least one to check).
        const alt = alternatives.find(a => g[a.prefix + a.fieldNames[0]] !== undefined);

        const gap = rawText.slice(prevEnd, m.index);
        const gapNewlines = countNewlines(gap);
        lineIndex += gapNewlines;

        if (gap.trim() && entries.length > 0) {
            const contLines = gap.split('\n').filter(l => l.length > 0);
            const last = entries[entries.length - 1];
            last.continuations.push(...contLines);
            last.displayLineCount += contLines.length;
        }

        const matchNewlines = countNewlines(m[0]);
        const field = (name) => alt ? g[alt.prefix + name] : undefined;

        entries.push({
            lineIndex,
            timestamp: (field('timestamp') ?? '').trim(),
            level:     (field('level')     ?? '').trim(),
            host:      field('host')    ?? '',
            pid:       parseInt(field('pid'),    10) || 0,
            threadId:  parseInt(field('thread'), 10) || 0,
            message:   field('message') ?? '',
            caller:    field('caller')  ?? '',
            extras:    [],
            continuations:    [],
            displayLineCount: 1 + matchNewlines,
            isPlainText:      false,
        });

        lineIndex += matchNewlines;
        prevEnd    = re.lastIndex;
    }

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
