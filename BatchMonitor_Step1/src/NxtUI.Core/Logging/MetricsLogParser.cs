using System.Globalization;
using System.Text.RegularExpressions;

namespace NxtUI.Core.Logging;

/// <summary>
/// Parses the memory-metrics lines emitted by MetricsTrackerService. A log file
/// contains many line formats; only lines carrying <see cref="Marker"/> are metrics.
///
/// Example line (top-level fields are pipe-delimited):
///   2026-06-19 21:23:16.6247|INFO|LDNPSM02Z03697|60136|209|Sending Metrics Tracker
///   message {stream}, {id}.Memory current usage: 290791424 byte, peak usage: 667439104
///   byte, child usage: 0, child peak usage: 639627264, Process start time "..."|caller
///
/// Top-level: timestamp | level | machine | pid | thread id | message | caller method
/// The message (field 6) carries the comma-delimited memory values.
/// </summary>
public static class MetricsLogParser
{
    public const string Marker = "Sending Metrics Tracker message";

    // Order matters only for "peak usage" vs "child peak usage": the negative
    // lookbehind keeps the parent "peak usage" from matching the child one.
    private static readonly Regex RxCurrent = new(@"current usage:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxPeak = new(@"(?<!child )peak usage:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxChild = new(@"child usage:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxChildPeak = new(@"child peak usage:\s*(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Returns true and a populated sample if the line is a metrics line.
    /// Non-metrics lines (any other format) return false.
    /// </summary>
    public static bool TryParse(string line, out MetricsSample? sample)
    {
        sample = null;
        if (string.IsNullOrEmpty(line) || !line.Contains(Marker, StringComparison.Ordinal))
            return false;

        var fields = line.Split('|');
        if (fields.Length < 7) return false;

        var message = fields[5];

        // Current usage is the anchor value — if it's missing this isn't a usable line.
        if (!TryLong(RxCurrent, message, out var current)) return false;
        TryLong(RxPeak, message, out var peak);
        TryLong(RxChild, message, out var child);
        TryLong(RxChildPeak, message, out var childPeak);

        DateTime.TryParse(fields[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts);

        sample = new MetricsSample
        {
            Timestamp = ts,
            CurrentUsageBytes = current,
            PeakUsageBytes = peak,
            ChildUsageBytes = child,
            ChildPeakUsageBytes = childPeak,
        };
        return true;
    }

    private static bool TryLong(Regex rx, string input, out long value)
    {
        value = 0;
        var m = rx.Match(input);
        return m.Success && long.TryParse(m.Groups[1].Value, out value);
    }
}
