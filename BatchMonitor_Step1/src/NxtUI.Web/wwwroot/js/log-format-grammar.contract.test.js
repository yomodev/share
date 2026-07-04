import { describe, it, expect } from 'vitest'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { compileFormat } from './log-viewer-parser.js'

// Contract test: the placeholder grammar ({timestamp}, {level}, {*}, {$}, ...) is
// implemented independently in compileFormat() here and in
// LogBrowserService.CompileFormat (C#). Both sides assert against the same fixture
// (tests/shared/log-format-grammar-cases.json) — see
// tests/NxtUI.Tests/Services/LogFormatGrammarContractTests.cs for the C# side.
// A grammar change made on only one side will fail whichever side's assertions no
// longer match the shared fixture.

const here = dirname(fileURLToPath(import.meta.url))
const fixturePath = join(here, '..', '..', '..', '..', 'tests', 'shared', 'log-format-grammar-cases.json')
const cases = JSON.parse(readFileSync(fixturePath, 'utf-8'))

describe('compileFormat — shared grammar fixture', () => {
    for (const c of cases) {
        it(c.name, () => {
            const compiled = compileFormat(c.format)

            if (!c.valid) {
                expect(compiled).toBeNull()
                return
            }

            expect(compiled).not.toBeNull()
            compiled.regex.lastIndex = 0
            const match = compiled.regex.exec(c.line)
            expect(match).not.toBeNull()

            for (const [field, expected] of Object.entries(c.expected)) {
                expect(match.groups?.[field]).toBe(expected)
            }
        })
    }
})
