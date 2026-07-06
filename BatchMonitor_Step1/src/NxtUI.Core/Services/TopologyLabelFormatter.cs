using System.Text.RegularExpressions;

namespace NxtUI.Core.Services;

/// <summary>
/// Shortens service/pipeline labels for display in the run-detail flow graph by removing
/// configured filler words (see <c>UiSettings.GraphLabelStripWords</c>). This affects the
/// rendered <c>Label</c>/<c>DisplayName</c> only — never the raw names used as topology and
/// edge-matching keys.
/// </summary>
public static class TopologyLabelFormatter
{
    // Collapse a run of 2+ separators (left behind after a word is cut out of the middle
    // of a name, e.g. "Ccr_AXIS-DEV1_Incr" -> "Ccr__Incr") down to a single separator.
    private static readonly Regex DoubledSeparators = new(@"[_-]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Removes each of <paramref name="words"/> (case-insensitive) from <paramref name="raw"/>,
    /// substituting the current environment id for the <c>{EnvID}</c> token, then collapses
    /// doubled separators and trims leading/trailing <c>-</c>/<c>_</c>/spaces. Never returns an
    /// empty string — if stripping would remove everything, the original is kept.
    /// </summary>
    public static string Strip(string? raw, IReadOnlyList<string>? words, string? envId)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
        if (words is null || words.Count == 0) return raw;

        var result = raw;
        foreach (var word in words)
        {
            var actual = word == "{EnvID}" ? envId : word;
            if (string.IsNullOrEmpty(actual)) continue;
            result = Regex.Replace(result, Regex.Escape(actual), string.Empty, RegexOptions.IgnoreCase);
        }

        // Collapse "__" / "--" / "-_" runs to the run's first character, then trim edges.
        result = DoubledSeparators.Replace(result, m => m.Value[0].ToString());
        result = result.Trim(' ', '-', '_');

        // Guard against a name made entirely of strip-words collapsing to nothing.
        return result.Length == 0 ? raw : result;
    }

    /// <summary>Convenience factory: a reusable formatter closed over a word list and env id.</summary>
    public static Func<string, string> Build(IReadOnlyList<string>? words, string? envId) =>
        raw => Strip(raw, words, envId);
}
