import { describe, it, expect } from 'vitest'
import { parse, evaluate } from './filter.js'

const FIELDS = ['ChunkId', 'Service', 'Pipeline']

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
        const node = parse('ChunkId:abc', FIELDS)
        expect(node.type).toBe('field')
        expect(node.field).toBe('ChunkId')
    })

    it('field name lookup is case-insensitive', () => {
        expect(parse('chunkid:abc', FIELDS).field).toBe('ChunkId')
    })

    it('alias "chunk" resolves to ChunkId', () => {
        expect(parse('chunk:0114', FIELDS).field).toBe('ChunkId')
    })

    it(':= produces Exact matchType', () => {
        expect(parse('ChunkId:=abc', FIELDS).matchType).toBe('Exact')
    })

    it('wildcard * produces Glob matchType', () => {
        expect(parse('ChunkId:chk-*', FIELDS).matchType).toBe('Glob')
    })

    it('null keyword produces IsNull matchType', () => {
        expect(parse('ChunkId:null', FIELDS).matchType).toBe('IsNull')
    })
})

describe('parse — quoted strings', () => {
    it("single-quoted string is case-insensitive (caseSensitive=false)", () => {
        const node = parse("ChunkId:'ABC'", FIELDS)
        expect(node.caseSensitive).toBe(false)
        expect(node.value.value).toBe('ABC')
    })

    it('double-quoted string is case-sensitive', () => {
        const node = parse('ChunkId:"ABC"', FIELDS)
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
    ])('ChunkId:%s produces matchType %s', (expr, expected) => {
        expect(parse(`ChunkId:${expr}`, FIELDS).matchType).toBe(expected)
    })

    it('range 1..10 produces Between matchType with RangeValue', () => {
        const node = parse('ChunkId:1..10', FIELDS)
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
        expect(eval_('ChunkId:0114', { ChunkId: 'chk-0114' })).toBe(true)
    })

    it('does not match absent substring', () => {
        expect(eval_('ChunkId:9999', { ChunkId: 'chk-0114' })).toBe(false)
    })

    it('leading-zero string matches as substring', () => {
        expect(eval_('0114', { ChunkId: 'chk-0114' })).toBe(true)
    })

    it('plain number (114) matches as substring in string field', () => {
        // 114 is parsed as NumberValue but Contains still does string match
        expect(eval_('114', { ChunkId: 'chk-0114' })).toBe(true)
    })

    it('bare term matches any searchable field', () => {
        expect(eval_('svcA', { Service: 'svcA' })).toBe(true)
        expect(eval_('svcA', { Pipeline: 'svcA-pipeline' })).toBe(true)
        expect(eval_('svcA', { ChunkId: 'svcA-001' })).toBe(true)
    })
})

describe('evaluate — Exact', () => {
    it('requires full value match', () => {
        expect(eval_('ChunkId:=chk-0114', { ChunkId: 'chk-0114' })).toBe(true)
        expect(eval_('ChunkId:=chk',      { ChunkId: 'chk-0114' })).toBe(false)
    })

    it('is case-insensitive by default', () => {
        expect(eval_('ChunkId:=CHK-0114', { ChunkId: 'chk-0114' })).toBe(true)
    })
})

describe('evaluate — Glob', () => {
    it('* matches any suffix', () => {
        expect(eval_('ChunkId:chk-*',   { ChunkId: 'chk-0114' })).toBe(true)
        expect(eval_('ChunkId:chk-*',   { ChunkId: 'other-0114' })).toBe(false)
    })

    it('? matches a single character', () => {
        expect(eval_('ChunkId:chk-011?', { ChunkId: 'chk-0114' })).toBe(true)
        expect(eval_('ChunkId:chk-011?', { ChunkId: 'chk-01145' })).toBe(false)
    })
})

describe('evaluate — IsNull', () => {
    it('matches empty string', () => {
        expect(eval_('ChunkId:null', { ChunkId: '' })).toBe(true)
    })

    it('does not match non-empty string', () => {
        expect(eval_('ChunkId:null', { ChunkId: 'abc' })).toBe(false)
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
