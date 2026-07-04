using System.Runtime.CompilerServices;
using System.Text.Json;
using NxtUI.Web.Services;

namespace NxtUI.Tests.Services;

/// <summary>
/// Contract test: LogBrowserService.CompileFormat (C#) and log-viewer-parser.js's
/// compileFormat() are two hand-maintained implementations of the same placeholder
/// grammar. This runs a shared set of format strings + sample lines
/// (tests/shared/log-format-grammar-cases.json) through the C# side and asserts the
/// extracted field values — the JS side asserts the same fixture independently in
/// log-format-grammar.contract.test.js. If a future grammar change updates one side
/// without the other, whichever side's assertions drift from the fixture will fail.
/// </summary>
public sealed class LogFormatGrammarContractTests
{
    public sealed record Case(string Name, string Format, string Line, bool Valid, Dictionary<string, string>? Expected);

    public static IEnumerable<object[]> Cases()
    {
        foreach (var c in LoadCases())
            yield return [c];
    }

    private static List<Case> LoadCases()
    {
        var json = File.ReadAllText(FixturePath());
        using var doc = JsonDocument.Parse(json);

        var cases = new List<Case>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name   = el.GetProperty("name").GetString()!;
            var format = el.GetProperty("format").GetString()!;
            var line   = el.GetProperty("line").GetString()!;
            var valid  = el.GetProperty("valid").GetBoolean();

            Dictionary<string, string>? expected = null;
            if (el.TryGetProperty("expected", out var expEl))
            {
                expected = new Dictionary<string, string>();
                foreach (var prop in expEl.EnumerateObject())
                    expected[prop.Name] = prop.Value.GetString() ?? "";
            }

            cases.Add(new Case(name, format, line, valid, expected));
        }
        return cases;
    }

    private static string FixturePath([CallerFilePath] string here = "")
    {
        // tests/NxtUI.Tests/Services/<this file>.cs -> ascend to tests/, then shared/.
        var dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", "..", "shared", "log-format-grammar-cases.json"));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CompileFormat_MatchesFixtureExpectations(Case testCase)
    {
        var compiled = LogBrowserService.CompileFormat(testCase.Format);

        if (!testCase.Valid)
        {
            Assert.Null(compiled);
            return;
        }

        Assert.NotNull(compiled);
        var match = compiled!.Regex.Match(testCase.Line);
        Assert.True(match.Success, $"[{testCase.Name}] format did not match sample line");

        foreach (var (field, expectedValue) in testCase.Expected!)
        {
            var group = match.Groups[field];
            Assert.True(group.Success, $"[{testCase.Name}] field '{field}' did not capture");
            Assert.Equal(expectedValue, group.Value);
        }
    }
}
