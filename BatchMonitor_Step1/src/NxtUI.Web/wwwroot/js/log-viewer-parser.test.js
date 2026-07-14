import { describe, it, expect } from 'vitest'
import {
    isTimestampLine, buildRegex, findMatches, highlightText, escapeHtml,
    compileFormat, compileMultiFormat, parseMultiFormat, detectFormat,
} from './log-viewer-parser.js'

// Legacy 7-field pipe format used by the ENTRIES fixture below (ts|level|host|pid|thread|message|caller).
const LEGACY_FORMAT = '{timestamp}|{level}|{host}|{pid}|{thread}|{message}|{caller}{*}{$}'
const parseLog = (raw) => parseMultiFormat(raw, compileMultiFormat([LEGACY_FORMAT]))

// ── isTimestampLine ──────────────────────────────────────────────────────────

describe('isTimestampLine', () => {
    it('recognises ISO date with space', ()  => expect(isTimestampLine('2024-01-15 12:00:00')).toBe(true))
    it('recognises ISO date with T',    ()  => expect(isTimestampLine('2024-01-15T12:00:00')).toBe(true))
    it('rejects continuation line',     ()  => expect(isTimestampLine('   at MyMethod()')).toBe(false))
    it('rejects empty string',          ()  => expect(isTimestampLine('')).toBe(false))
    it('rejects partial date',          ()  => expect(isTimestampLine('2024-01-15')).toBe(false))
})

// ── parseLog — empty / trivial inputs ────────────────────────────────────────

describe('parseLog — empty input', () => {
    it('returns [] for empty string', () => expect(parseLog('')).toEqual([]))
    it('returns [] for null',         () => expect(parseLog(null)).toEqual([]))
    it('returns [] for undefined',    () => expect(parseLog(undefined)).toEqual([]))
})

// ── parseLog — single line ────────────────────────────────────────────────────

describe('parseLog — single entry, no pipes in message', () => {
    const raw = '2024-01-15 12:00:00|INFO|srv-01|1234|42|Application started|MyApp.Startup'
    const entries = parseLog(raw)

    it('produces exactly one entry',  () => expect(entries).toHaveLength(1))
    it('parses timestamp',            () => expect(entries[0].timestamp).toBe('2024-01-15 12:00:00'))
    it('parses level',                () => expect(entries[0].level).toBe('INFO'))
    it('parses host',                 () => expect(entries[0].host).toBe('srv-01'))
    it('parses pid',                  () => expect(entries[0].pid).toBe(1234))
    it('parses threadId',             () => expect(entries[0].threadId).toBe(42))
    it('parses message',              () => expect(entries[0].message).toBe('Application started'))
    it('parses caller',               () => expect(entries[0].caller).toBe('MyApp.Startup'))
    it('has no continuations',        () => expect(entries[0].continuations).toEqual([]))
    it('displayLineCount is 1',       () => expect(entries[0].displayLineCount).toBe(1))
    it('lineIndex is 0',              () => expect(entries[0].lineIndex).toBe(0))
})

// ── parseLog — trailing junk after caller is absorbed by the {*} catch-all ──────

describe('parseLog — extra fields after caller', () => {
    const raw = '2024-01-15 12:00:01|WARN|srv-01|1234|17|Something happened|MyApp.Check|extra-a|extra-b'
    const entries = parseLog(raw)

    it('produces one entry',           () => expect(entries).toHaveLength(1))
    it('message is captured',          () => expect(entries[0].message).toBe('Something happened'))
    // caller is the last capture and has no literal delimiter before the trailing {*},
    // so it greedily absorbs the rest of the line (extras aren't split out separately).
    it('caller absorbs trailing extras',() => expect(entries[0].caller).toBe('MyApp.Check|extra-a|extra-b'))
    it('threadId is correct',          () => expect(entries[0].threadId).toBe(17))
})

// ── parseLog — multi-line entry (stack trace) ─────────────────────────────────

describe('parseLog — continuation lines', () => {
    const raw = [
        '2024-01-15 12:00:02|ERROR|srv-01|1234|55|Unhandled exception|MyApp.Worker',
        '   System.InvalidOperationException: Something went wrong',
        '   at MyApp.Worker.ProcessAsync() in Worker.cs:42',
        '   at System.Threading.Tasks.Task.Run()',
    ].join('\n')

    const entries = parseLog(raw)

    it('groups into one entry',              () => expect(entries).toHaveLength(1))
    it('has 3 continuation lines',           () => expect(entries[0].continuations).toHaveLength(3))
    it('displayLineCount is 4',              () => expect(entries[0].displayLineCount).toBe(4))
    it('first continuation is the exception line', () =>
        expect(entries[0].continuations[0]).toBe('   System.InvalidOperationException: Something went wrong'))
    it('last continuation is the Task.Run line', () =>
        expect(entries[0].continuations[2]).toBe('   at System.Threading.Tasks.Task.Run()'))
})

// ── parseLog — multiple entries ───────────────────────────────────────────────

describe('parseLog — multiple entries in sequence', () => {
    const raw = [
        '2024-01-15 12:00:00|INFO|srv-01|1234|10|Started|MyApp.Boot',
        '2024-01-15 12:00:01|DEBUG|srv-01|1234|11|Processing|MyApp.Worker',
        '2024-01-15 12:00:02|ERROR|srv-01|1234|12|Failed|MyApp.Worker',
        '   at something()',
    ].join('\n')

    const entries = parseLog(raw)

    it('produces 3 entries',             () => expect(entries).toHaveLength(3))
    it('second entry is DEBUG',          () => expect(entries[1].level).toBe('DEBUG'))
    it('third entry has 1 continuation', () => expect(entries[2].continuations).toHaveLength(1))
    it('lineIndex of second entry is 1', () => expect(entries[1].lineIndex).toBe(1))
    it('lineIndex of third entry is 2',  () => expect(entries[2].lineIndex).toBe(2))
    it('threadIds are independent',      () => {
        expect(entries[0].threadId).toBe(10)
        expect(entries[1].threadId).toBe(11)
        expect(entries[2].threadId).toBe(12)
    })
})

// ── parseLog — trailing newline ───────────────────────────────────────────────

describe('parseLog — trailing newline', () => {
    const raw = '2024-01-15 12:00:00|INFO|srv-01|1234|42|Hello|Caller\n'
    it('does not produce a phantom empty entry', () => expect(parseLog(raw)).toHaveLength(1))
})

// ── parseLog — noise before first entry ──────────────────────────────────────

describe('parseLog — lines before first timestamp', () => {
    const raw = 'garbage line\nanother garbage\n2024-01-15 12:00:00|INFO|srv-01|1234|7|Hello|Caller'
    it('discards pre-header noise',  () => expect(parseLog(raw)).toHaveLength(1))
    it('entry has correct message',  () => expect(parseLog(raw)[0].message).toBe('Hello'))
    it('entry has correct threadId', () => expect(parseLog(raw)[0].threadId).toBe(7))
})

// ── buildRegex ────────────────────────────────────────────────────────────────

describe('buildRegex', () => {
    it('returns null for empty term',       () => expect(buildRegex('')).toBeNull())
    it('returns a RegExp for plain term',   () => expect(buildRegex('hello')).toBeInstanceOf(RegExp))
    it('is case-insensitive by default',    () => expect(buildRegex('hello').flags).toContain('i'))
    it('is case-sensitive when requested',  () => expect(buildRegex('hello', { caseSensitive: true }).flags).not.toContain('i'))
    it('escapes special chars in plain mode', () => {
        const re = buildRegex('a.b')
        expect(re.test('axb')).toBe(false)
        expect(re.test('a.b')).toBe(true)
    })
    it('treats term as regex when isRegex=true', () => {
        const re = buildRegex('a.b', { isRegex: true })
        expect(re.test('axb')).toBe(true)
    })
    it('throws SyntaxError on invalid regex', () => {
        expect(() => buildRegex('[broken', { isRegex: true })).toThrow(SyntaxError)
    })
    it('always includes g flag', () => {
        expect(buildRegex('x').flags).toContain('g')
    })
})

// ── findMatches ───────────────────────────────────────────────────────────────

const ENTRIES = parseLog([
    '2024-01-15 12:00:00|INFO|srv-01|1234|71|Application started|Boot',
    '2024-01-15 12:00:01|ERROR|srv-02|5678|83|Database connection failed|DbInit',
    '   caused by: timeout after 30s',
    '2024-01-15 12:00:02|WARN|srv-01|1234|95|Retry attempt 1|RetryPolicy',
].join('\n'))

describe('findMatches — basic search', () => {
    it('finds by message substring',     () => expect(findMatches(ENTRIES, buildRegex('started'))).toEqual([0]))
    it('finds by level',                 () => expect(findMatches(ENTRIES, buildRegex('error'))).toEqual([1]))
    it('finds by host',                  () => expect(findMatches(ENTRIES, buildRegex('srv-02'))).toEqual([1]))
    it('finds by caller',                () => expect(findMatches(ENTRIES, buildRegex('RetryPolicy'))).toEqual([2]))
    it('finds by threadId',              () => expect(findMatches(ENTRIES, buildRegex('83'))).toEqual([1]))
    it('returns empty array when no match', () => expect(findMatches(ENTRIES, buildRegex('xyzzy'))).toEqual([]))
    it('finds match in continuation line',  () => expect(findMatches(ENTRIES, buildRegex('timeout'))).toEqual([1]))
    it('finds multiple entries',            () => expect(findMatches(ENTRIES, buildRegex('srv-01'))).toEqual([0, 2]))
})

describe('findMatches — case sensitivity', () => {
    it('matches regardless of case by default',  () => expect(findMatches(ENTRIES, buildRegex('DATABASE'))).toEqual([1]))
    it('does not match wrong case when sensitive', () =>
        expect(findMatches(ENTRIES, buildRegex('DATABASE', { caseSensitive: true }))).toEqual([]))
    it('matches exact case when sensitive',       () =>
        expect(findMatches(ENTRIES, buildRegex('Database', { caseSensitive: true }))).toEqual([1]))
})

describe('findMatches — regex mode', () => {
    it('matches with regex pattern',    () => expect(findMatches(ENTRIES, buildRegex('Retry.+1', { isRegex: true }))).toEqual([2]))
    it('matches with anchored pattern', () => expect(findMatches(ENTRIES, buildRegex('^2024', { isRegex: true }))).toEqual([0, 1, 2]))
})

// ── highlightText ─────────────────────────────────────────────────────────────

describe('highlightText', () => {
    it('returns escaped HTML when re is null', () => {
        expect(highlightText('a & b', null)).toBe('a &amp; b')
    })

    it('wraps a single match in <mark>', () => {
        const re = buildRegex('hello')
        expect(highlightText('say hello world', re)).toBe('say <mark class="lv-hl">hello</mark> world')
    })

    it('wraps multiple matches', () => {
        const re = buildRegex('x')
        expect(highlightText('x and x', re)).toBe('<mark class="lv-hl">x</mark> and <mark class="lv-hl">x</mark>')
    })

    it('escapes HTML before highlighting', () => {
        const re = buildRegex('bold')
        const result = highlightText('<bold> text', re)
        expect(result).toContain('&lt;')
        expect(result).toContain('<mark class="lv-hl">bold</mark>')
    })

    it('is case-insensitive when re has i flag', () => {
        const re = buildRegex('hello')
        expect(highlightText('HELLO world', re)).toBe('<mark class="lv-hl">HELLO</mark> world')
    })
})

// ── escapeHtml ────────────────────────────────────────────────────────────────

describe('escapeHtml', () => {
    it('escapes &',  () => expect(escapeHtml('a & b')).toBe('a &amp; b'))
    it('escapes <',  () => expect(escapeHtml('<tag>')).toBe('&lt;tag&gt;'))
    it('escapes "',  () => expect(escapeHtml('"val"')).toBe('&quot;val&quot;'))
    it('leaves plain text unchanged', () => expect(escapeHtml('hello')).toBe('hello'))
    it('accepts a number (pid/threadId are integers)', () => expect(escapeHtml(1234)).toBe('1234'))
    it('accepts zero', () => expect(escapeHtml(0)).toBe('0'))
})

// ── compileFormat — placeholder token behaviour ───────────────────────────────
// This is the format-string mini-language documented in appsettings.json's
// Logs:Formats comment: {timestamp}/{level}/{host}/{pid}/{thread}/{message}/{caller}
// are captures, {*} matches-but-doesn't-capture, {$} is a zero-width end-of-line
// assertion, {newline} matches a literal line break (multi-line templates only).

describe('compileFormat — placeholder tokens', () => {
    it('returns null for an empty/falsy format string', () => {
        expect(compileFormat('')).toBeNull()
        expect(compileFormat(null)).toBeNull()
    })

    it('returns null when fewer than 2 fields are captured (too few to be useful)', () => {
        expect(compileFormat('{timestamp} only one field')).toBeNull()
    })

    it('captures all 7 named fields when the template declares them', () => {
        const fmt = compileFormat('{timestamp}|{level}|{host}|{pid}|{thread}|{message}|{caller}')
        expect(fmt.fieldNames).toEqual(['timestamp', 'level', 'host', 'pid', 'thread', 'message', 'caller'])
    })

    it('{*} matches but does not capture — absent from fieldNames', () => {
        // A literal delimiter is needed between a capture and a following {*} to give
        // the capture a stop boundary — with no delimiter, the capture (being last with
        // no literal after it) greedily absorbs what {*} would have matched, and {*}
        // itself matches the empty remainder. This is exercised with a delimiter here.
        const fmt = compileFormat('{timestamp}|{level}|{message}|{*}')
        expect(fmt.fieldNames).not.toContain('*')
        const m = fmt.regex.exec('2024-01-15|INFO|hello|trailing junk here')
        expect(m).not.toBeNull()
        expect(m.groups.message).toBe('hello')
    })

    it('{$} asserts end-of-line without consuming or capturing anything', () => {
        // Without {$}, a trailing {*} would already be greedy to end-of-line on its own
        // (see buildPatternBody's "last token: greedy to EOL" stopper) — {$} is only
        // meaningful as an explicit anchor after a capture that would otherwise be
        // followed by nothing, making the intent (and the match boundary) explicit.
        const fmt = compileFormat('{timestamp}|{level}|{message}{$}')
        const re = new RegExp(fmt.regex.source, fmt.regex.flags)
        const m = re.exec('2024-01-15|INFO|hello world')
        expect(m).not.toBeNull()
        expect(m.groups.message).toBe('hello world')
        expect(m[0].length).toBe('2024-01-15|INFO|hello world'.length) // {$} itself matched zero characters
    })

    it('{newline} matches a literal line break for multi-line templates', () => {
        const fmt = compileFormat('BEGIN{newline}{timestamp}|{level}|{message}{newline}END')
        expect(fmt.hasNewline).toBe(true)
        const re = new RegExp(fmt.regex.source, fmt.regex.flags)
        const m = re.exec('BEGIN\n2024-01-15|INFO|hello\nEND')
        expect(m).not.toBeNull()
        expect(m.groups.message).toBe('hello')
    })

    it('a capture stops at the next literal character, not past it', () => {
        const fmt = compileFormat('{timestamp}|{level}|{message}|{caller}')
        const re = new RegExp(fmt.regex.source, fmt.regex.flags)
        const m = re.exec('2024-01-15|INFO|hello|MyApp.Boot')
        expect(m.groups.level).toBe('INFO')
        expect(m.groups.message).toBe('hello')
        expect(m.groups.caller).toBe('MyApp.Boot')
    })
})

// ── compileMultiFormat / parseMultiFormat — the actual regression this section exists for ──
//
// Real log files can genuinely mix line shapes (most lines omit an optional field like
// {host}, a minority include it) — a single "best" format chosen for the WHOLE file
// necessarily gets one of the two shapes wrong: either the with-host lines lose their
// host (wrong format won), or the no-host lines are silently dropped entirely (the
// with-host format won but doesn't match the no-host shape at all). compileMultiFormat +
// parseMultiFormat exist specifically so each line is matched against whichever
// configured format actually describes IT.

const HOST_FORMAT    = '{timestamp}|{level}|{host}|{pid}|{thread}|{message}|{caller}{*}{$}'
const NO_HOST_FORMAT = '{timestamp}|{level}|{thread}|{message}|{caller}{*}{$}'
const MULTILINE_FORMAT =
    '----- BEGIN {*}{newline}{timestamp}{newline}{level}|{host}|{pid}|{thread}|{message}|{caller}|{*}{newline}----- END {*}{newline}.{newline}'

describe('compileMultiFormat', () => {
    it('returns null for an empty or all-invalid format list', () => {
        expect(compileMultiFormat([])).toBeNull()
        expect(compileMultiFormat(null)).toBeNull()
        expect(compileMultiFormat(['{onefield}'])).toBeNull()
    })

    it('skips an invalid/trivial format but keeps the valid ones', () => {
        const multi = compileMultiFormat(['{onefield}', HOST_FORMAT])
        expect(multi.alternatives).toHaveLength(1)
        expect(multi.alternatives[0].formatStr).toBe(HOST_FORMAT)
    })

    it('gives each alternative a unique group-name prefix', () => {
        const multi = compileMultiFormat([HOST_FORMAT, NO_HOST_FORMAT])
        expect(multi.alternatives[0].prefix).not.toBe(multi.alternatives[1].prefix)
    })
})

describe('parseMultiFormat — the mixed-shape regression', () => {
    it('correctly captures host on with-host lines AND parses no-host lines in the same file', () => {
        const multi = compileMultiFormat([HOST_FORMAT, NO_HOST_FORMAT])
        const lines = []
        for (let i = 0; i < 20; i++) {
            lines.push(`2024-01-15 12:00:${String(i).padStart(2, '0')}|INFO|11|Processing item ${i}|MyApp.Worker`)
        }
        lines.push('2024-01-15 12:00:30|WARN|srv-01|1234|12|Retry attempt|MyApp.RetryPolicy')
        lines.push('2024-01-15 12:00:31|ERROR|srv-02|5678|13|Failed|MyApp.Worker')
        const entries = parseMultiFormat(lines.join('\n'), multi)

        expect(entries).toHaveLength(22) // every line parsed — none silently dropped
        expect(entries[0].host).toBe('')
        expect(entries[0].message).toBe('Processing item 0')
        expect(entries[20].host).toBe('srv-01')
        expect(entries[20].pid).toBe(1234)
        expect(entries[20].message).toBe('Retry attempt')
        expect(entries[21].host).toBe('srv-02')
        expect(entries[21].message).toBe('Failed')
    })

    it('does not misalign fields on the with-host lines when the no-host shape is the majority', () => {
        // The specific failure mode this whole section guards against: an earlier
        // length-only detection heuristic could pick the SHORTER (no-host) format for
        // the whole file, which then mis-parsed the with-host lines by shifting every
        // field after the missing one — the host value landing in `message`, and the
        // real message landing in `caller`.
        const multi = compileMultiFormat([HOST_FORMAT, NO_HOST_FORMAT])
        const lines = Array.from({ length: 10 }, (_, i) =>
            `2024-01-15 12:00:${String(i).padStart(2, '0')}|INFO|11|Processing item ${i}|MyApp.Worker`)
        lines.push('2024-01-15 12:00:30|WARN|srv-01|1234|12|Retry attempt|MyApp.RetryPolicy')
        const entries = parseMultiFormat(lines.join('\n'), multi)
        const withHost = entries[entries.length - 1]
        expect(withHost.host).toBe('srv-01')
        expect(withHost.message).toBe('Retry attempt') // NOT "1234" (the pid, shifted into message by the wrong template)
        expect(withHost.caller).toBe('MyApp.RetryPolicy') // NOT "12|Retry attempt|MyApp.RetryPolicy"
    })

    it('handles a multi-line format combined with single-line formats in the same file', () => {
        const multi = compileMultiFormat([MULTILINE_FORMAT, HOST_FORMAT, NO_HOST_FORMAT])
        const raw = [
            '----- BEGIN some-header-junk',
            '2024-01-15 12:00:00',
            'INFO|srv-01|1234|10|Multi-line entry message|MyApp.Boot|extra',
            '----- END trailer-junk',
            '.',
            '2024-01-15 12:00:01|WARN|11|No host here|MyApp.Worker',
            '2024-01-15 12:00:02|ERROR|srv-02|5678|12|Failed|MyApp.Worker',
        ].join('\n')
        const entries = parseMultiFormat(raw, multi)

        expect(entries).toHaveLength(3)
        expect(entries[0]).toMatchObject({ host: 'srv-01', message: 'Multi-line entry message' })
        expect(entries[1]).toMatchObject({ host: '', message: 'No host here' })
        expect(entries[2]).toMatchObject({ host: 'srv-02', message: 'Failed' })
    })

    it('returns [] for empty input', () => {
        const multi = compileMultiFormat([HOST_FORMAT])
        expect(parseMultiFormat('', multi)).toEqual([])
    })

    it('falls back to plain-text mode when multiCompiled is null', () => {
        const entries = parseMultiFormat('just some text\nanother line', null)
        expect(entries).toHaveLength(2)
        expect(entries[0].isPlainText).toBe(true)
        expect(entries[0].message).toBe('just some text')
    })

    it('a line matching none of the configured formats becomes a continuation of the previous entry', () => {
        const multi = compileMultiFormat([HOST_FORMAT])
        const raw = [
            '2024-01-15 12:00:00|INFO|srv-01|1234|10|Started|MyApp.Boot',
            'this line matches nothing configured',
        ].join('\n')
        const entries = parseMultiFormat(raw, multi)
        expect(entries).toHaveLength(1)
        expect(entries[0].continuations).toEqual(['this line matches nothing configured'])
    })
})

// ── detectFormat — single "best" format selection (still exported; used for display/
// labeling purposes now that actual parsing goes through compileMultiFormat instead) ──

describe('detectFormat', () => {
    it('returns null when nothing matches', () => {
        expect(detectFormat('nonsense', [compileFormat(HOST_FORMAT)])).toBeNull()
    })

    it('prefers the format with more fields when both match confidently', () => {
        const formats = [compileFormat(HOST_FORMAT), compileFormat(NO_HOST_FORMAT)]
        const lines = Array.from({ length: 5 }, (_, i) =>
            `2024-01-15 12:00:0${i}|INFO|srv-01|1234|1${i}|msg${i}|Caller`)
        const detected = detectFormat(lines.join('\n'), formats)
        expect(detected.formatStr).toBe(HOST_FORMAT)
    })

    it('prefers a format matched at least twice over one matched only once, regardless of field count', () => {
        const formats = [compileFormat(HOST_FORMAT), compileFormat(NO_HOST_FORMAT)]
        const lines = [
            '2024-01-15 12:00:00|INFO|srv-01|1234|10|Started|MyApp.Boot', // matches HOST_FORMAT once
            '2024-01-15 12:00:01|INFO|11|Processing|MyApp.Worker',        // matches NO_HOST_FORMAT
            '2024-01-15 12:00:02|INFO|12|Processing|MyApp.Worker',        // matches NO_HOST_FORMAT again
        ]
        const detected = detectFormat(lines.join('\n'), formats)
        expect(detected.formatStr).toBe(NO_HOST_FORMAT)
    })
})
