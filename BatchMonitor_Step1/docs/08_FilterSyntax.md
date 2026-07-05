# 08 — Filter Syntax

## Overview

A unified filter language used across the entire application. The same syntax works in every filter textbox — timeline, batch list, and any future view. The parser runs on the client (JS) and produces an **AST**. Depending on the context the AST is either:

- **Evaluated in-memory** (JS) — timeline and any view that filters already-loaded data
- **Sent to the server as JSON** and translated into a MongoDB `FilterDefinition` (C#) — views that query the database

The parser is written once. The server never parses filter strings — it only interprets ASTs.

---

## Operator Precedence

From highest to lowest:

| Level | Operator | Symbol |
|-------|----------|--------|
| 1 | NOT | `!` |
| 2 | AND | space |
| 3 | OR | `,` |
| — | Grouping | `( )` |

Parentheses override all precedence rules.

**Examples:**

```
dog cat, plane fish
→ (dog AND cat) OR (plane AND fish)

!error cat, plane
→ (!error AND cat) OR (plane)

(dog, cat) fish
→ (dog OR cat) AND fish
```

---

## Terms

### Bare word

Matches any object where **at least one searchable field** contains the word (case-insensitive).

```
Loader
```

### Global AND

Space between terms means AND at the **object level**: each term must be satisfied by *some* field, but they do not need to be in the same field.

```
Loader mongo
→ some_field contains "Loader"  AND  some_field contains "mongo"
  (the two fields can be different)
```

### OR

Comma separates OR alternatives. Spaces around the comma are ignored.

```
Loader , Transformer
Loader,Transformer
```

Both are equivalent.

### NOT

`!` prefix. Applies to the next token or parenthesised group.

```
!Loader
!svc:Loader
!(Loader, Transformer)
```

---

## Wildcards (Glob)

| Pattern | Meaning |
|---------|---------|
| `word*` | starts-with |
| `*word` | ends-with |
| `*word*` | contains (same as bare `word`) |
| `d?g` | `?` matches exactly one character |
| `Lo?d*` | composable |

```
Load*               starts with "Load"
*mapper             ends with "mapper"
Lo?der              "Loader", "Londer", "Looder" …
*chunk*             contains "chunk" (same as bare chunk)
```

---

## Field Targeting

Syntax: `field:value` or `alias:value`. Both the short alias and the full property name are accepted.

### Known aliases

| Alias | Full name | Model |
|-------|-----------|-------|
| `svc` | `service` | PerformanceEvent |
| `pipe` | `pipeline` | PerformanceEvent |
| `srv` | `server` | PerformanceEvent |
| `pid` | `processid` | PerformanceEvent |
| `src` | `source` | PerformanceEvent |
| `chunk` | `chunkid` | PerformanceEvent |
| `batch` | `batchname` | BatchSummary |
| `type` | `type` | BatchSummary |
| `run` | `runid` | BatchSummary |

Field names are case-insensitive in the filter expression.

### Contains (default)

```
svc:Loader
service:Loader        (same)
pipe:mongo*
srv:node?eu*
```

### Exact match

Prefix the value with `=`. Wildcards are not allowed with `=` — a parse error is thrown.

```
svc:=Loader           exact, case-insensitive
svc:="Loader"         exact, case-sensitive
svc:=Load*            PARSE ERROR — wildcards not allowed with =
```

### Null / empty check

The keyword `null` tests whether a field is null or missing.

```
finish:null           field is null or missing
!finish:null          field has a value
error:null            no error set
!error:null           error is set
```

---

## Field-Scoped Groups

When parentheses follow a field prefix, every bare term inside is scoped to that field. The same AND/OR rules apply inside.

```
name:(dog, cat, "RAT")
→ name contains "dog"  OR  name contains "cat"  OR  name contains "RAT" (case-sensitive)

name:(dog cat)
→ name contains "dog"  AND  name contains "cat"  (both in the same field)

svc:(Loader, Transformer) !is:error
→ (service is Loader OR Transformer) AND not an error

duration:(>=5 <10, 2)
→ (duration >= 5 AND duration < 10)  OR  duration == 2
```

---

## Numeric Comparisons

Applies to any numeric field.

| Syntax | Meaning |
|--------|---------|
| `records:>100` | greater than |
| `records:>=50` | greater than or equal |
| `records:<10` | less than |
| `records:<=10` | less than or equal |
| `records:50..200` | between 50 and 200 inclusive |
| `records:=42` | exact value |

```
records:(>0 <100, 0)
→ (records > 0 AND records < 100) OR records == 0
```

---

## Quotes and Case Sensitivity

| Syntax | Case | Spaces allowed |
|--------|------|----------------|
| `word` | insensitive | no |
| `'my phrase'` | insensitive | yes |
| `"My Phrase"` | **sensitive** | yes |

```
Loader                contains "loader" (any case)
"Loader"              contains "Loader" (exact case)
'field mapper'        contains "field mapper" (any case)
"Field Mapper"        contains "Field Mapper" (exact case)
svc:="DataProcessor"  service equals "DataProcessor" (exact case)
```

---

## Dates and Times

All date/time literals are interpreted as **local time** by default. Append `z` to indicate **UTC**.

### Relative offsets

No `z` suffix — relative offsets are always anchored to "now" and produce the same UTC moment regardless.

| Token | Meaning |
|-------|---------|
| `-30m` | 30 minutes ago |
| `-2h` | 2 hours ago |
| `-7d` | 7 days ago |
| `-2w` | 2 weeks ago |

### Named dates

| Token | Meaning |
|-------|---------|
| `now` | current moment, local |
| `nowz` | current moment, UTC |
| `today` | local midnight of today |
| `todayz` | UTC midnight of today |
| `yesterday` | local midnight of yesterday |
| `yesterdayz` | UTC midnight of yesterday |

### Absolute dates and times

| Token | Example |
|-------|---------|
| Date | `2024-01-15` / `2024-01-15z` |
| Datetime | `2024-01-15T09:30` / `2024-01-15T09:30z` |
| Time-of-day | `9:30` `09:30` `9:30:00` `09:30:00` |
| Time-of-day UTC | `9:30z` `09:30:00z` |

Time-of-day tokens mean "that time today" when used bare. They match the time-of-day component of a datetime field.

`z` on time-of-day means: interpret this time as UTC. Without `z` it is local.

### Date operators and ranges

```
start:>-1h                    started in the last hour
start:-7d..now                started in the last 7 days
start:today..now              started today (local)
start:yesterdayz..todayz      yesterday UTC to today UTC
start:>2024-01-15             after Jan 15
start:2024-01-15T09:00..2024-01-15T17:00
start:9:00..17:00             between 9am and 5pm today
finish:null                   not yet finished
!finish:null                  has finished
```

**Local→UTC conversion** happens on the client before the AST is serialized. The server always receives absolute UTC timestamps — it never handles timezone logic.

---

## Global Search and Searchable Fields

When a term has no field prefix it is matched against a **declared list of searchable fields** for the current view. This list is defined in JS alongside the filter component for each view.

```js
// timeline
searchableFields = ['Service', 'Pipeline', 'Source', 'Name', 'Server']

// batch list
searchableFields = ['BatchName', 'Type', 'RunId']
```

A bare term `Loader` on the timeline expands in the AST to:

```
OR(
  contains(Service,  "Loader"),
  contains(Pipeline, "Loader"),
  contains(Source,   "Loader"),
  contains(Name,  "Loader"),
  contains(Server,   "Loader")
)
```

The server receives only concrete field names — it has no concept of "global search". The same field list drives both the JS in-memory evaluator and the MongoDB query, so their behaviour is identical.

Fields not in the list are never scanned in a global term. This keeps MongoDB queries index-friendly and makes "search everything" intentional per view.

---

## Full Examples

```
# Find events from Loader service on mongo pipeline with no errors
svc:Loader pipe:mongo* error:null

# Events that started in the last hour, either Loader or Transformer
svc:(Loader, Transformer) start:>-1h

# Chunks with more than 100 records that are not yet finished
records:>100 finish:null

# Batches that failed or are still running
status:(Failed, Running)

# Exact service name, case-sensitive, large record count
svc:="DataProcessor" records:>=500

# Time range today (local), any service containing "proc"
start:9:00..17:00 svc:*proc*

# Anything mentioning "mongo" across all searchable fields, excluding errors
mongo !error:null

# Chunks that ran yesterday UTC, between two specific services
start:yesterdayz..todayz svc:(Loader, Transformer)

# Name contains "dog" or "cat" (case-insensitive) or exactly "RAT" (case-sensitive)
name:(dog, cat, "RAT")

# Duration between 5 and 10 exclusive, or exactly 2
duration:(>5 <10, 2)

# Pipeline starts with "mongo", server matches pattern node-X-eu
pipe:mongo* srv:node?eu*
```

---

## AST Shape

The parser produces a tree of nodes. Rough structure (language-agnostic):

```
Node =
  | GlobalTerm    { value: string, matchType: contains|startsWith|endsWith|glob }
  | FieldTerm     { field: string, value: string|number|date|null,
                    matchType: contains|exact|glob|null|comparison,
                    comparison?: > | >= | < | <= | ..,
                    caseSensitive: bool }
  | Not           { operand: Node }
  | And           { left: Node, right: Node }
  | Or            { left: Node, right: Node }
```

Global terms are expanded into an `Or` of `FieldTerm` nodes (one per searchable field) before the AST leaves the parser. The evaluators never see raw `GlobalTerm` nodes — the expansion is the parser's last step.

---

## Testing Strategy

### JS unit tests (parser + in-memory evaluator)

Test the parser in isolation: input string → expected AST.  
Test the evaluator in isolation: AST + object → true/false.

Corner cases to cover:

**Parser**
- Empty string → empty/no-op AST
- Single term, multi-term AND, OR, NOT
- Nested parentheses: `(a (b, c)) d`
- Field alias resolves to full name
- Unknown field alias → passed through as-is (no parse error; evaluator simply finds no matches)
- `:=` with wildcard → parse error
- Unclosed parenthesis → parse error
- Bare `null` keyword vs quoted `"null"` (literal string)
- `..` with reversed bounds: `100..50` (decide: error or swap)
- Time token formats: `9:30`, `09:30`, `9:30:00`, `09:30z`
- Relative offsets: `-30m`, `-2h`, `-7d`, `-2w`
- Named dates: `today`, `todayz`, `yesterday`
- `z` on relative offset is accepted but has no effect
- Glob patterns: `*`, `?`, combined `Lo?d*`
- Double vs single quotes: case sensitivity preserved in AST node
- Spaces around comma ignored
- Double/multiple spaces treated as single AND separator
- Field-scoped group: `name:(dog cat)` → AND, `name:(dog, cat)` → OR
- Global term expands to OR over searchable fields

**Evaluator**
- AND: both terms must match (across different fields)
- OR: either term matches
- NOT: negates correctly
- Global term: matches across fields, not just first
- `null` check: matches null and undefined/missing
- `!field:null`: matches only when field has a value
- Exact match: `=Loader` does not match `loader` (case-insensitive) and `="Loader"` rejects `loader`
- Glob `*`: zero or more chars
- Glob `?`: exactly one char, not zero
- `..` inclusive: bounds are included
- Comparison: `>`, `>=`, `<`, `<=` on numbers and dates
- Date comparison: local vs UTC correctly offset
- `today` matches midnight of today, not yesterday or tomorrow
- Empty searchable fields list → global term matches nothing

### C# unit tests (AST → MongoDB filter)

The C# tests receive a pre-built AST (deserialized from JSON) and assert the resulting `FilterDefinition` is correct. No filter string parsing in C#.

Corner cases to cover:

- `FieldTerm` contains → `$regex` with `i` flag
- `FieldTerm` contains, case-sensitive → `$regex` without `i` flag
- `FieldTerm` exact, case-insensitive → `$regex` `^value$` with `i` flag
- `FieldTerm` exact, case-sensitive → `$regex` `^value$` without `i` flag
- Glob `*` → `.*` in regex, `?` → `.` in regex
- `null` check → `{ field: null }` (matches null and missing)
- `!null` check → `{ field: { $ne: null } }`
- Numeric `>`, `>=`, `<`, `<=` → `$gt`, `$gte`, `$lt`, `$lte`
- Numeric `..` → `$gte` + `$lte` combined with `$and`
- `And` node → `$and`
- `Or` node → `$or`
- `Not` node → `$nor` with single element
- Alias already resolved (aliases are resolved client-side; C# receives full field names)
- Unknown field name → no filter applied for that term (yields no matches silently)
- Deeply nested AND/OR tree → correct nesting in Mongo filter
- Date values arrive as UTC ISO strings → parsed as UTC `DateTime`
- Empty `Or` list → matches nothing (`$nor: [{}]` or equivalent)
- Empty `And` list → matches everything (no filter)
