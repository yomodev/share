// Batch Monitor — Filter engine (ES module)
//
// Exports:
//   parse(input, searchableFields, aliases?) → FilterNode | null
//   evaluate(node, obj)                      → boolean
//   serialize(node)                          → string (JSON)
//
// The same AST shape is used by the C# side (MongoFilterBuilder).
// Global terms are expanded to an OrNode over searchableFields before
// the AST is returned, so callers never see a raw "global" node.

// ── Alias table (mirrors FilterParser.cs DefaultAliases) ──────────────────

const DEFAULT_ALIASES = {
    svc: 'Service',       service: 'Service',
    pipe: 'Pipeline',     pipeline: 'Pipeline',
    srv: 'Server',        server: 'Server',
    pid: 'ProcessId',     processid: 'ProcessId',
    src: 'Source',        source: 'Source',
    chunk: 'Name',        chunkid: 'Name',      name: 'Name',
    batch: 'BatchName',   batchname: 'BatchName',
    type: 'Type',
    run: 'RunId',         runid: 'RunId',
};

// ═══════════════════════════════════════════════════════════════════════════
// TOKENIZER
// ═══════════════════════════════════════════════════════════════════════════

/** @typedef {'word'|'quoted'|'not'|'comma'|'lparen'|'rparen'|'colon'|'coloneq'|'gt'|'gte'|'lt'|'lte'|'dotdot'|'eof'} TokKind */
/** @typedef {{ kind: TokKind, text: string, quoteChar?: string }} Token */

const WORD_STOPS = new Set([' ', '\t', '\r', '\n', ',', '(', ')', '!', ':', '"', "'", '>', '<']);

/**
 * @param {string} input
 * @returns {Token[]}
 */
function tokenize(input) {
    const tokens = [];
    let i = 0;
    const len = input.length;

    while (i < len) {
        const c = input[i];

        if (c === ' ' || c === '\t' || c === '\r' || c === '\n') { i++; continue; }

        switch (c) {
            case '!': tokens.push({ kind: 'not',    text: '!' }); i++; break;
            case ',': tokens.push({ kind: 'comma',  text: ',' }); i++; break;
            case '(': tokens.push({ kind: 'lparen', text: '(' }); i++; break;
            case ')': tokens.push({ kind: 'rparen', text: ')' }); i++; break;

            case '"': case "'": {
                const q = c; i++;
                let text = '';
                while (i < len && input[i] !== q) text += input[i++];
                if (i < len) i++;
                tokens.push({ kind: 'quoted', text, quoteChar: q });
                break;
            }

            case ':':
                if (input[i + 1] === '=') { tokens.push({ kind: 'coloneq', text: ':=' }); i += 2; }
                else                      { tokens.push({ kind: 'colon',   text: ':' });  i++;    }
                break;

            case '>':
                if (input[i + 1] === '=') { tokens.push({ kind: 'gte', text: '>=' }); i += 2; }
                else                      { tokens.push({ kind: 'gt',  text: '>' });  i++;    }
                break;

            case '<':
                if (input[i + 1] === '=') { tokens.push({ kind: 'lte', text: '<=' }); i += 2; }
                else                      { tokens.push({ kind: 'lt',  text: '<' });  i++;    }
                break;

            case '.':
                if (input[i + 1] === '.') { tokens.push({ kind: 'dotdot', text: '..' }); i += 2; break; }
                // fall through to word
            default: {
                let text = '';
                while (i < len) {
                    const ch = input[i];
                    if (ch === '.' && input[i + 1] === '.') break;
                    // Allow ':' inside a word only when it looks like a time literal (digit:digit).
                    if (ch === ':' && text.length > 0 && /\d$/.test(text) && /^\d/.test(input[i + 1] ?? '')) {
                        text += ch; i++; continue;
                    }
                    if (WORD_STOPS.has(ch)) break;
                    text += input[i++];
                }
                if (text) tokens.push({ kind: 'word', text });
                break;
            }
        }
    }

    tokens.push({ kind: 'eof', text: '' });
    return tokens;
}

// ═══════════════════════════════════════════════════════════════════════════
// PARSE CONTEXT
// ═══════════════════════════════════════════════════════════════════════════

class ParseCtx {
    /** @param {Token[]} tokens */
    constructor(tokens) {
        this._tokens = tokens;
        this._pos    = 0;
    }
    get current() { return this._tokens[this._pos]; }
    peek(offset = 1) { return this._tokens[Math.min(this._pos + offset, this._tokens.length - 1)]; }
    consume()  { return this._tokens[this._pos++]; }
    expect(kind) {
        const t = this.current;
        if (t.kind !== kind) throw new Error(`FilterParser: expected ${kind} but got '${t.text}'`);
        return this.consume();
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// RECURSIVE DESCENT PARSER
// ═══════════════════════════════════════════════════════════════════════════

/**
 * @typedef {{ type: 'and', left: FilterNode, right: FilterNode }} AndNode
 * @typedef {{ type: 'or',  left: FilterNode, right: FilterNode }} OrNode
 * @typedef {{ type: 'not', operand: FilterNode }}                 NotNode
 * @typedef {{ type: 'field', field: string, matchType: string, caseSensitive: boolean, value: FilterValue }} FieldNode
 * @typedef {AndNode|OrNode|NotNode|FieldNode} FilterNode
 *
 * @typedef {{ type: 'string', value: string }}              StringValue
 * @typedef {{ type: 'number', value: number }}              NumberValue
 * @typedef {{ type: 'date',   value: string }}              DateValue   (UTC ISO string)
 * @typedef {{ type: 'null' }}                               NullValue
 * @typedef {{ type: 'range',  low: FilterValue, high: FilterValue }} RangeValue
 * @typedef {StringValue|NumberValue|DateValue|NullValue|RangeValue} FilterValue
 */

/**
 * @param {ParseCtx} ctx
 * @param {string[]} searchableFields
 * @param {Record<string,string>} aliases
 * @returns {FilterNode|null}
 */
function parseOr(ctx, searchableFields, aliases) {
    let left = parseAnd(ctx, searchableFields, aliases);
    while (ctx.current.kind === 'comma') {
        ctx.consume();
        const right = parseAnd(ctx, searchableFields, aliases);
        if (left === null)       left = right;
        else if (right !== null) left = { type: 'or', left, right };
    }
    return left;
}

function parseAnd(ctx, searchableFields, aliases) {
    let left = null;
    while (ctx.current.kind !== 'comma' && ctx.current.kind !== 'rparen' && ctx.current.kind !== 'eof') {
        const right = parseNot(ctx, searchableFields, aliases);
        if (right === null) break;
        left = left === null ? right : { type: 'and', left, right };
    }
    return left;
}

function parseNot(ctx, searchableFields, aliases) {
    if (ctx.current.kind !== 'not') return parsePrimary(ctx, searchableFields, aliases);
    ctx.consume();
    const operand = parseNot(ctx, searchableFields, aliases);
    return operand === null ? null : { type: 'not', operand };
}

function parsePrimary(ctx, searchableFields, aliases) {
    if (ctx.current.kind === 'lparen') {
        ctx.consume();
        const inner = parseOr(ctx, searchableFields, aliases);
        ctx.expect('rparen');
        return inner;
    }
    return parseTerm(ctx, searchableFields, aliases);
}

function parseTerm(ctx, searchableFields, aliases) {
    if (ctx.current.kind === 'word') {
        const next = ctx.peek();
        if (next.kind === 'colon' || next.kind === 'coloneq')
            return parseFieldTerm(ctx, aliases);
    }
    return parseBareTerm(ctx, searchableFields);
}

function parseFieldTerm(ctx, aliases) {
    const raw   = ctx.consume().text;
    const field = resolveAlias(raw, aliases);
    const exact = ctx.current.kind === 'coloneq';
    ctx.consume(); // ':' or ':='

    if (!exact && ctx.current.kind === 'lparen') {
        ctx.consume();
        const group = parseFieldScopedOr(ctx, field, aliases);
        ctx.expect('rparen');
        return group;
    }

    return parseFieldValue(ctx, field, exact);
}

function parseFieldScopedOr(ctx, field, aliases) {
    let left = parseFieldScopedAnd(ctx, field);
    while (ctx.current.kind === 'comma') {
        ctx.consume();
        const right = parseFieldScopedAnd(ctx, field);
        if (left === null)       left = right;
        else if (right !== null) left = { type: 'or', left, right };
    }
    return left;
}

function parseFieldScopedAnd(ctx, field) {
    let left = null;
    while (ctx.current.kind !== 'comma' && ctx.current.kind !== 'rparen' && ctx.current.kind !== 'eof') {
        const right = parseFieldValue(ctx, field, false);
        if (right === null) break;
        left = left === null ? right : { type: 'and', left, right };
    }
    return left;
}

function parseFieldValue(ctx, field, exact) {
    const CMP = { gt: 'GreaterThan', gte: 'GreaterThanOrEqual', lt: 'LessThan', lte: 'LessThanOrEqual' };
    const cmpOp = CMP[ctx.current.kind] ?? null;
    if (cmpOp) ctx.consume();

    const { value, caseSensitive } = parseValueAtom(ctx);
    if (value === null) return null;

    if (exact && value.type === 'string' && hasWildcard(value.value))
        throw new Error(`FilterParser: wildcards not allowed with exact match (:=): '${value.value}'`);

    if (!cmpOp && !exact && ctx.current.kind === 'dotdot') {
        ctx.consume();
        const { value: high } = parseValueAtom(ctx);
        if (high === null) throw new Error("FilterParser: expected value after '..'");
        return fieldNode(field, 'Between', false, { type: 'range', low: value, high });
    }

    if (cmpOp) return fieldNode(field, cmpOp, false, value);
    if (exact)  return fieldNode(field, 'Exact', caseSensitive, value);
    if (value.type === 'null') return fieldNode(field, 'IsNull', false, value);

    if (value.type === 'string') {
        const matchType = hasWildcard(value.value) ? 'Glob' : 'Contains';
        return fieldNode(field, matchType, caseSensitive, value);
    }

    return fieldNode(field, 'Exact', false, value);
}

function parseBareTerm(ctx, searchableFields) {
    const { value, caseSensitive } = parseValueAtom(ctx);
    if (value === null || searchableFields.length === 0) return null;

    const matchType =
        value.type === 'null'   ? 'IsNull'
      : value.type === 'string' && hasWildcard(value.value) ? 'Glob'
      : value.type === 'string' ? 'Contains'
      : 'Contains';

    let result = null;
    for (const f of searchableFields) {
        const term = fieldNode(f, matchType, caseSensitive, value);
        result = result === null ? term : { type: 'or', left: result, right: term };
    }
    return result;
}

// ── Value atom ─────────────────────────────────────────────────────────────

function parseValueAtom(ctx) {
    const tok = ctx.current;

    if (tok.kind === 'quoted') {
        ctx.consume();
        return { value: { type: 'string', value: tok.text }, caseSensitive: tok.quoteChar === '"' };
    }

    if (tok.kind === 'word') {
        ctx.consume();
        return { value: interpretWord(tok.text), caseSensitive: false };
    }

    return { value: null, caseSensitive: false };
}

function interpretWord(text) {
    if (text.toLowerCase() === 'null') return { type: 'null' };

    // Leading-zero strings (e.g. "0114", "007") are identifiers, not numbers.
    const num = Number(text);
    if (!isNaN(num) && text.trim() !== '' && !/^0\d/.test(text))
        return { type: 'number', value: num };

    const result = tryParseDate(text);
    if (result !== null) return result;

    return { type: 'string', value: text };
}

// ── Date parsing ───────────────────────────────────────────────────────────

const RE_RELATIVE = /^(-?)(\d+)([mhdw])$/i;
const RE_TIME     = /^(\d{1,2}):(\d{2})(?::(\d{2}))?z?$/i;
const RE_ISO      = /^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2})?)?z?$/i;

/**
 * Returns a FilterValue or null.
 * Time-only inputs (HH:MM or HH:MM:SS) return { type: 'time', seconds }
 * so comparison ignores the date and matches only the time-of-day portion.
 * Everything else returns { type: 'date', value: isoString }.
 */
function tryParseDate(text) {
    const lower = text.toLowerCase();

    switch (lower) {
        case 'now':  case 'nowz':        return { type: 'date', value: new Date().toISOString() };
        case 'today': case 'todayz': {
            const d = new Date(); d.setUTCHours(0,0,0,0);
            return { type: 'date', value: d.toISOString() };
        }
        case 'yesterday': case 'yesterdayz': {
            const d = new Date(); d.setUTCHours(0,0,0,0); d.setUTCDate(d.getUTCDate()-1);
            return { type: 'date', value: d.toISOString() };
        }
    }

    const rel = RE_RELATIVE.exec(text);
    if (rel) {
        const negative = rel[1] === '-';
        const n        = parseInt(rel[2], 10);
        const unit     = rel[3].toLowerCase();
        if (negative) {
            const now = new Date();
            switch (unit) {
                case 'm': now.setMinutes(now.getMinutes() - n); break;
                case 'h': now.setHours(now.getHours()     - n); break;
                case 'd': now.setDate(now.getDate()       - n); break;
                case 'w': now.setDate(now.getDate()       - n * 7); break;
            }
            return { type: 'date', value: now.toISOString() };
        } else {
            const d = new Date();
            d.setUTCHours(0, 0, 0, 0);
            switch (unit) {
                case 'm': d.setUTCMinutes(d.getUTCMinutes() + n); break;
                case 'h': d.setUTCHours(d.getUTCHours()     + n); break;
                case 'd': d.setUTCDate(d.getUTCDate()       + n); break;
                case 'w': d.setUTCDate(d.getUTCDate()       + n * 7); break;
            }
            return { type: 'date', value: d.toISOString() };
        }
    }

    // Time-only: 9:30 or 09:30:00 — compare only time-of-day, date-agnostic
    const tm = RE_TIME.exec(text);
    if (tm && !text.startsWith('-')) {
        const h = parseInt(tm[1], 10);
        const m = parseInt(tm[2], 10);
        const s = tm[3] ? parseInt(tm[3], 10) : 0;
        return { type: 'time', seconds: h * 3600 + m * 60 + s };
    }

    // ISO date / datetime
    if (RE_ISO.test(text)) {
        const normalised = text.replace(/z$/i, '') + 'Z';
        const d = new Date(normalised);
        if (!isNaN(d.getTime())) return { type: 'date', value: d.toISOString() };
    }

    return null;
}

// ── Helpers ────────────────────────────────────────────────────────────────

function hasWildcard(str) { return str.includes('*') || str.includes('?'); }

function fieldNode(field, matchType, caseSensitive, value) {
    return { type: 'field', field, matchType, caseSensitive, value };
}

function resolveAlias(raw, aliases) {
    return aliases[raw.toLowerCase()] ?? raw;
}

// ═══════════════════════════════════════════════════════════════════════════
// EVALUATOR  (client-side, in-memory)
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Tests whether a plain JS object satisfies the filter AST.
 * @param {FilterNode|null} node
 * @param {Record<string, unknown>} obj
 * @returns {boolean}
 */
export function evaluate(node, obj) {
    if (node === null) return true;
    switch (node.type) {
        case 'and':   return evaluate(node.left, obj) && evaluate(node.right, obj);
        case 'or':    return evaluate(node.left, obj) || evaluate(node.right, obj);
        case 'not':   return !evaluate(node.operand, obj);
        case 'field': return evalField(node, obj);
        default:      return true;
    }
}

/** @param {FieldNode} node @param {Record<string, unknown>} obj */
function evalField(node, obj) {
    const raw = obj[node.field];

    if (node.matchType === 'IsNull') return raw == null || raw === '';

    if (raw == null) return false;

    const val = node.value;

    switch (node.matchType) {
        case 'Contains': {
            const haystack = String(raw);
            const needle   = String(val.value);
            return node.caseSensitive
                ? haystack.includes(needle)
                : haystack.toLowerCase().includes(needle.toLowerCase());
        }

        case 'Exact': {
            const a = String(raw), b = String(val.value);
            return node.caseSensitive ? a === b : a.toLowerCase() === b.toLowerCase();
        }

        case 'Glob': {
            const pattern = globToRegex(String(val.value), node.caseSensitive);
            return pattern.test(String(raw));
        }

        case 'GreaterThan':        return compareValues(raw, val) >  0;
        case 'GreaterThanOrEqual': return compareValues(raw, val) >= 0;
        case 'LessThan':           return compareValues(raw, val) <  0;
        case 'LessThanOrEqual':    return compareValues(raw, val) <= 0;

        case 'Between': {
            const low  = compareValues(raw, val.low);
            const high = compareValues(raw, val.high);
            return low >= 0 && high <= 0;
        }

        default: return false;
    }
}

function compareValues(rawFieldValue, filterValue) {
    if (filterValue.type === 'number') {
        return Number(rawFieldValue) - filterValue.value;
    }
    if (filterValue.type === 'date') {
        const a = rawFieldValue instanceof Date ? rawFieldValue : new Date(String(rawFieldValue).replace(' ', 'T'));
        const b = new Date(filterValue.value);
        return a.getTime() - b.getTime();
    }
    if (filterValue.type === 'time') {
        // Compare only the time-of-day portion — ignores the date entirely.
        const raw = String(rawFieldValue);
        // Extract HH:MM:SS from "YYYY-MM-DD HH:MM:SS" or ISO strings.
        const m = raw.match(/(\d{1,2}):(\d{2})(?::(\d{2}))?/);
        if (!m) return -1;
        const aSeconds = parseInt(m[1], 10) * 3600 + parseInt(m[2], 10) * 60 + (m[3] ? parseInt(m[3], 10) : 0);
        return aSeconds - filterValue.seconds;
    }
    return String(rawFieldValue).localeCompare(String(filterValue.value));
}

function globToRegex(pattern, caseSensitive) {
    let re = '^';
    for (const ch of pattern) {
        if (ch === '*') re += '.*';
        else if (ch === '?') re += '.';
        else re += ch.replace(/[.+^${}()|[\]\\]/g, '\\$&');
    }
    re += '$';
    return new RegExp(re, caseSensitive ? '' : 'i');
}

// ═══════════════════════════════════════════════════════════════════════════
// PUBLIC API
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Parses a filter string and returns an AST ready for the evaluator or for
 * JSON serialization and sending to the server.
 *
 * @param {string} input
 * @param {string[]} searchableFields  Fields to match when no field prefix is given.
 * @param {Record<string,string>} [aliases]  Alias→field mapping; defaults to the standard table.
 * @returns {FilterNode|null}
 */
export function parse(input, searchableFields, aliases = DEFAULT_ALIASES) {
    if (!input || !input.trim()) return null;
    const tokens = tokenize(input);
    const ctx    = new ParseCtx(tokens);
    return parseOr(ctx, searchableFields, aliases);
}

/**
 * Serializes an AST to a JSON string suitable for sending to the server.
 * @param {FilterNode|null} node
 * @returns {string}
 */
export function serialize(node) {
    return JSON.stringify(node);
}
