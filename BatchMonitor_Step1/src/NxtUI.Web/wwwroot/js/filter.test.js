import { describe, it, expect } from 'vitest'
import { parse, evaluate } from './filter.js'
import { parseLog } from './log-viewer-parser.js'

const FIELDS = ['Name', 'Service', 'Pipeline']

// Helper: collect all leaf FieldNodes from an AST tree
function leaves(node) {
    if (!node) return []
    if (node.type === 'field') return [node]
    if (node.type === 'or' || node.type === 'and') return [...leaves(node.left), ...leaves(node.right)]
    if (node.type === 'not') return leaves(node.operand)
    return []
}

// ── Parser tests ───────────────────────────────────────────────────────────

describe('parse — empty input', () => {
    it('returns null for empty string', () => expect(parse('', FIELDS)).toBeNull())
    it('returns null for whitespace',   () => expect(parse('   ', FIELDS)).toBeNull())
})

describe('parse — bare term expansion', () => {
    it('expands to OR across all searchable fields', () => {
        expect(parse('hello', FIELDS).type).toBe('or')
    })

    it('all leaves have Contains matchType for plain string', () => {
        const ls = leaves(parse('hello', FIELDS))
        expect(ls.every(l => l.matchType === 'Contains')).toBe(true)
        expect(ls.every(l => l.value.value === 'hello')).toBe(true)
        expect(ls.map(l => l.field).sort()).toEqual([...FIELDS].sort())
    })

    it('leading-zero string is treated as string, not number', () => {
        const ls = leaves(parse('0114', FIELDS))
        expect(ls.every(l => l.value.type === 'string')).toBe(true)
        expect(ls.every(l => l.value.value === '0114')).toBe(true)
    })

    it('plain integer is treated as number value', () => {
        const ls = leaves(parse('42', FIELDS))
        expect(ls.every(l => l.value.type === 'number')).toBe(true)
        expect(ls.every(l => l.value.value === 42)).toBe(true)
    })
})

describe('parse — field-scoped terms', () => {
    it('field:term produces a single field node', () => {
        const node = parse('Name:abc', FIELDS)
        expect(node.type).toBe('field')
        expect(node.field).toBe('Name')
    })

    it('field name lookup is case-insensitive', () => {
        expect(parse('chunkid:abc', FIELDS).field).toBe('Name')
    })

    it('alias "chunk" resolves to Name', () => {
        expect(parse('chunk:0114', FIELDS).field).toBe('Name')
    })

    it(':= produces Exact matchType', () => {
        expect(parse('Name:=abc', FIELDS).matchType).toBe('Exact')
    })

    it('wildcard * produces Glob matchType', () => {
        expect(parse('Name:chk-*', FIELDS).matchType).toBe('Glob')
    })

    it('null keyword produces IsNull matchType', () => {
        expect(parse('Name:null', FIELDS).matchType).toBe('IsNull')
    })
})

describe('parse — quoted strings', () => {
    it("single-quoted string is case-insensitive (caseSensitive=false)", () => {
        const node = parse("Name:'ABC'", FIELDS)
        expect(node.caseSensitive).toBe(false)
        expect(node.value.value).toBe('ABC')
    })

    it('double-quoted string is case-sensitive', () => {
        const node = parse('Name:"ABC"', FIELDS)
        expect(node.caseSensitive).toBe(true)
    })
})

describe('parse — boolean operators', () => {
    it('space between terms is implicit AND', () => {
        expect(parse('Service:svcA Pipeline:pipe1', FIELDS).type).toBe('and')
    })

    it('comma separates OR alternatives', () => {
        expect(parse('Service:svcA, Service:svcB', FIELDS).type).toBe('or')
    })

    it('! produces NOT node', () => {
        expect(parse('!Service:svcA', FIELDS).type).toBe('not')
    })
})

describe('parse — comparison operators', () => {
    it.each([
        ['>5',  'GreaterThan'],
        ['>=5', 'GreaterThanOrEqual'],
        ['<5',  'LessThan'],
        ['<=5', 'LessThanOrEqual'],
    ])('Name:%s produces matchType %s', (expr, expected) => {
        expect(parse(`Name:${expr}`, FIELDS).matchType).toBe(expected)
    })

    it('range 1..10 produces Between matchType with RangeValue', () => {
        const node = parse('Name:1..10', FIELDS)
        expect(node.matchType).toBe('Between')
        expect(node.value.type).toBe('range')
    })
})

// ── Evaluator tests ────────────────────────────────────────────────────────

function eval_(filter, obj) {
    return evaluate(parse(filter, FIELDS), obj)
}

describe('evaluate — null node', () => {
    it('null node matches everything', () => {
        expect(evaluate(null, {})).toBe(true)
    })
})

describe('evaluate — Contains', () => {
    it('matches substring case-insensitively', () => {
        expect(eval_('Name:0114', { Name: 'chk-0114' })).toBe(true)
    })

    it('does not match absent substring', () => {
        expect(eval_('Name:9999', { Name: 'chk-0114' })).toBe(false)
    })

    it('leading-zero string matches as substring', () => {
        expect(eval_('0114', { Name: 'chk-0114' })).toBe(true)
    })

    it('plain number (114) matches as substring in string field', () => {
        // 114 is parsed as NumberValue but Contains still does string match
        expect(eval_('114', { Name: 'chk-0114' })).toBe(true)
    })

    it('bare term matches any searchable field', () => {
        expect(eval_('svcA', { Service: 'svcA' })).toBe(true)
        expect(eval_('svcA', { Pipeline: 'svcA-pipeline' })).toBe(true)
        expect(eval_('svcA', { Name: 'svcA-001' })).toBe(true)
    })
})

describe('evaluate — Exact', () => {
    it('requires full value match', () => {
        expect(eval_('Name:=chk-0114', { Name: 'chk-0114' })).toBe(true)
        expect(eval_('Name:=chk',      { Name: 'chk-0114' })).toBe(false)
    })

    it('is case-insensitive by default', () => {
        expect(eval_('Name:=CHK-0114', { Name: 'chk-0114' })).toBe(true)
    })
})

describe('evaluate — Glob', () => {
    it('* matches any suffix', () => {
        expect(eval_('Name:chk-*',   { Name: 'chk-0114' })).toBe(true)
        expect(eval_('Name:chk-*',   { Name: 'other-0114' })).toBe(false)
    })

    it('? matches a single character', () => {
        expect(eval_('Name:chk-011?', { Name: 'chk-0114' })).toBe(true)
        expect(eval_('Name:chk-011?', { Name: 'chk-01145' })).toBe(false)
    })
})

describe('evaluate — IsNull', () => {
    it('matches empty string', () => {
        expect(eval_('Name:null', { Name: '' })).toBe(true)
    })

    it('does not match non-empty string', () => {
        expect(eval_('Name:null', { Name: 'abc' })).toBe(false)
    })
})

describe('evaluate — numeric comparisons', () => {
    function evalNum(filter, count) {
        return evaluate(parse(filter, ['Count']), { Count: count })
    }

    it.each([
        ['>5',  6, true],
        ['>5',  5, false],
        ['>=5', 5, true],
        ['<5',  4, true],
        ['<5',  5, false],
        ['<=5', 5, true],
    ])('Count:%s with value %d → %s', (expr, value, expected) => {
        expect(evalNum(`Count:${expr}`, value)).toBe(expected)
    })

    it('between matches inclusive range', () => {
        const node = parse('Count:1..10', ['Count'])
        expect(evaluate(node, { Count: 1  })).toBe(true)
        expect(evaluate(node, { Count: 10 })).toBe(true)
        expect(evaluate(node, { Count: 11 })).toBe(false)
    })
})

describe('log viewer — full pipeline (parseLog → filter with integer pid/threadId)', () => {
    const LOG_SEARCH_FIELDS = ['level', 'host', 'pid', 'threadId', 'message', 'caller'];
    const LOG_ALIASES = { lvl: 'level', msg: 'message', tid: 'threadId', ts: 'timestamp' };

    const raw = [
        '2024-01-15 12:00:00|INFO|srv-01|30|10|msg A|Caller',
        '2024-01-15 12:00:01|WARN|srv-02|80|20|msg B|Caller',
        '2024-01-15 12:00:02|ERROR|srv-01|200|5|msg C|Caller',
        '2024-01-15 12:00:03|DEBUG|srv-02|500|99|msg D|Caller',
    ].join('\n')

    const entries = parseLog(raw)

    function filter(expr) {
        const node = parse(expr, LOG_SEARCH_FIELDS, LOG_ALIASES)
        return entries.filter(e => evaluate(node, e))
    }

    it('pid values are integers', () => {
        expect(typeof entries[0].pid).toBe('number')
        expect(entries[0].pid).toBe(30)
    })
    it('threadId values are integers', () => {
        expect(typeof entries[0].threadId).toBe('number')
        expect(entries[0].threadId).toBe(10)
    })
    it('pid:>50 returns entries with pid > 50', () => {
        const r = filter('pid:>50')
        expect(r.map(e => e.pid)).toEqual([80, 200, 500])
    })
    it('pid:<100 returns entries with pid < 100', () => {
        const r = filter('pid:<100')
        expect(r.map(e => e.pid)).toEqual([30, 80])
    })
    it('pid:50..300 returns entries in range', () => {
        const r = filter('pid:50..300')
        expect(r.map(e => e.pid)).toEqual([80, 200])
    })
    it('tid:>15 returns entries with threadId > 15', () => {
        const r = filter('tid:>15')
        expect(r.map(e => e.threadId)).toEqual([20, 99])
    })
    it('lvl:ERROR returns only error entries', () => {
        expect(filter('lvl:ERROR').map(e => e.level)).toEqual(['ERROR'])
    })
    it('ts:>12:00:00 returns entries after noon (time-only, date-agnostic)', () => {
        // entries: 12:00:00, 12:00:01, 12:00:02, 12:00:03
        const r = filter('ts:>12:00:00')
        expect(r.map(e => e.timestamp)).toEqual([
            '2024-01-15 12:00:01',
            '2024-01-15 12:00:02',
            '2024-01-15 12:00:03',
        ])
    })
    it('ts:12:00:01..12:00:02 returns entries in time range', () => {
        const r = filter('ts:12:00:01..12:00:02')
        expect(r.map(e => e.timestamp)).toEqual([
            '2024-01-15 12:00:01',
            '2024-01-15 12:00:02',
        ])
    })
})

describe('evaluate — boolean operators', () => {
    it('AND requires both conditions (space = AND)', () => {
        expect(eval_('Service:svcA Pipeline:pipe1', { Service: 'svcA', Pipeline: 'pipe1' })).toBe(true)
        expect(eval_('Service:svcA Pipeline:pipe1', { Service: 'svcA', Pipeline: 'other' })).toBe(false)
    })

    it('OR requires at least one condition (comma = OR)', () => {
        expect(eval_('Service:svcA, Service:svcB', { Service: 'svcB' })).toBe(true)
        expect(eval_('Service:svcA, Service:svcB', { Service: 'svcC' })).toBe(false)
    })

    it('NOT negates condition', () => {
        expect(eval_('!Service:svcA', { Service: 'svcB' })).toBe(true)
        expect(eval_('!Service:svcA', { Service: 'svcA' })).toBe(false)
    })
})

// ── Date parsing ───────────────────────────────────────────────────────────

const DATE_FIELDS = ['UpdatedDateTime']

function parsedDate(expr) {
    const node = leaves(parse(expr, DATE_FIELDS))[0]
    return node?.value?.type === 'date' ? new Date(node.value.value) : null
}

describe('parse — negative relative offset (now − N)', () => {
    it('-10m resolves to ~10 minutes before now', () => {
        const before = Date.now()
        const d = parsedDate('UpdatedDateTime:>-10m')
        const after  = Date.now()
        expect(d).not.toBeNull()
        expect(d.getTime()).toBeGreaterThanOrEqual(before - 10 * 60 * 1000 - 1000)
        expect(d.getTime()).toBeLessThanOrEqual(after)
    })

    it('-2h resolves to ~2 hours before now', () => {
        const approx = Date.now() - 2 * 60 * 60 * 1000
        const d = parsedDate('UpdatedDateTime:>-2h')
        expect(Math.abs(d.getTime() - approx)).toBeLessThan(5000)
    })
})

describe('parse — positive relative offset (today + N)', () => {
    it('10m resolves to today at 00:10:00 UTC', () => {
        const d = parsedDate('UpdatedDateTime:<10m')
        expect(d).not.toBeNull()
        const today = new Date()
        today.setUTCHours(0, 0, 0, 0)
        expect(d.getTime()).toBe(today.getTime() + 10 * 60 * 1000)
    })

    it('2h resolves to today at 02:00:00 UTC', () => {
        const d = parsedDate('UpdatedDateTime:<2h')
        const today = new Date()
        today.setUTCHours(0, 0, 0, 0)
        expect(d.getTime()).toBe(today.getTime() + 2 * 60 * 60 * 1000)
    })
})

describe('parse — time-only hh:mm produces time type (date-agnostic)', () => {
    it('00:10 produces { type: time, seconds: 600 }', () => {
        const node = leaves(parse('UpdatedDateTime:<00:10', ['UpdatedDateTime']))[0]
        expect(node?.value).toEqual({ type: 'time', seconds: 600 })
    })
    it('19:06:24 produces correct seconds', () => {
        const node = leaves(parse('UpdatedDateTime:>19:06:24', ['UpdatedDateTime']))[0]
        expect(node?.value).toEqual({ type: 'time', seconds: 19 * 3600 + 6 * 60 + 24 })
    })
})

describe('parse — positive relative offset still produces date type', () => {
    it('10m produces a date value (not time)', () => {
        const node = leaves(parse('UpdatedDateTime:<10m', ['UpdatedDateTime']))[0]
        expect(node?.value?.type).toBe('date')
    })
})
