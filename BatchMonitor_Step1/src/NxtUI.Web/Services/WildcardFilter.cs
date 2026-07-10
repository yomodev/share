using System.Text.RegularExpressions;

namespace NxtUI.Web.Services;

/// <summary>
/// Turns a user-typed filter pattern into a substring-search regex. Deliberately NOT
/// anchored with ^...$ — plain text like "auth" behaves like a normal "contains" filter
/// rather than requiring an exact full-string match. * and ? still work as real
/// wildcards for callers who want to anchor/constrain further (e.g. "auth*").
/// </summary>
public static class WildcardFilter
{
    public static Regex ToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex(escaped, RegexOptions.IgnoreCase);
    }
}
