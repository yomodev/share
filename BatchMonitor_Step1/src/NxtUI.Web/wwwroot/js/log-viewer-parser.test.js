import { describe, it, expect } from 'vitest'
import { parseLog, isTimestampLine, buildRegex, findMatches, highlightText, escapeHtml } from './log-viewer-parser.js'

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
    it('parses pid',                  () => expect(entries[0].pid).toBe('1234'))
    it('parses threadId',             () => expect(entries[0].threadId).toBe('42'))
    it('parses message',              () => expect(entries[0].message).toBe('Application started'))
    it('parses caller',               () => expect(entries[0].caller).toBe('MyApp.Startup'))
    it('has no continuations',        () => expect(entries[0].continuations).toEqual([]))
    it('displayLineCount is 1',       () => expect(entries[0].displayLineCount).toBe(1))
    it('lineIndex is 0',              () => expect(entries[0].lineIndex).toBe(0))
})

// ── parseLog — pipe inside message ───────────────────────────────────────────

describe('parseLog — pipe character inside message', () => {
    const raw = '2024-01-15 12:00:01|WARN|srv-01|1234|17|Value is a|b|c here|MyApp.Check'
    const entries = parseLog(raw)

    it('produces one entry',          () => expect(entries).toHaveLength(1))
    it('reconstructs full message',   () => expect(entries[0].message).toBe('Value is a|b|c here'))
    it('caller is the last segment',  () => expect(entries[0].caller).toBe('MyApp.Check'))
    it('threadId is correct',         () => expect(entries[0].threadId).toBe('17'))
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
        expect(entries[0].threadId).toBe('10')
        expect(entries[1].threadId).toBe('11')
        expect(entries[2].threadId).toBe('12')
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
    it('entry has correct threadId', () => expect(parseLog(raw)[0].threadId).toBe('7'))
})

// ── parseLog — undersized entry (fewer than 7 fields) ────────────────────────

describe('parseLog — fewer than 7 pipe-separated fields', () => {
    const raw = '2024-01-15 12:00:00|INFO|srv-01|1234|JustMessage'
    const entries = parseLog(raw)

    it('still produces one entry',   () => expect(entries).toHaveLength(1))
    it('message is the 5th field',   () => expect(entries[0].message).toBe('JustMessage'))
    it('caller is empty string',     () => expect(entries[0].caller).toBe(''))
    it('threadId is empty string',   () => expect(entries[0].threadId).toBe(''))
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
})
