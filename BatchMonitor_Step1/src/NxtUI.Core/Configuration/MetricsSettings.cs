namespace NxtUI.Core.Configuration;

/// <summary>
/// Settings for per-service memory-metrics collection (<see cref="MetricsSourceMode"/> —
/// consumed by NxtUI.Web's ServiceMetricsMonitor). Bound from appsettings.json "Metrics" section.
/// </summary>
public class MetricsSettings
{
    public const string SectionName = "Metrics";

    /// <summary>
    /// Where memory metrics are pulled from. See <see cref="MetricsSourceMode"/>.
    /// Default: <see cref="MetricsSourceMode.Both"/>.
    /// </summary>
    public MetricsSourceMode Source { get; set; } = MetricsSourceMode.Both;

    /// <summary>
    /// Per-environment Kafka topic that services publish periodic memory-metrics samples to
    /// (protobuf-encoded MetricsTracker messages), single partition, low retention.
    /// ServiceMetricsMonitor tails this live and only falls back to disk-based log parsing
    /// for history older than the topic's retention window. Default: "metrics".
    /// </summary>
    public string TopicName { get; set; } = "metrics";

    /// <summary>
    /// Fixed name of the file inside the folder resolved via FileBrowser:ServiceTemplates that
    /// contains the process memory-metrics lines (see MetricsLogParser). The folder is unique
    /// per service/PID, so this filename is constant across services. Example: "Metrics.log".
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>How often (seconds) to re-read the on-disk metrics file for new lines. Default: 90.</summary>
    public int IntervalSeconds { get; set; } = 90;

    /// <summary>
    /// Minutes of inactivity (no subscribers) after which cached metrics data (file offsets +
    /// sample history) is released from memory. During the idle window polling is paused but
    /// data is preserved so re-subscribing resumes from where it left off without re-reading
    /// files from the start. Default: 10.
    /// </summary>
    public int IdleReleaseMinutes { get; set; } = 10;
}

/// <summary>
/// Controls where NxtUI.Web's ServiceMetricsMonitor pulls per-service memory metrics from.
/// </summary>
public enum MetricsSourceMode
{
    /// <summary>
    /// Kafka (live) plus a one-time on-disk backfill for the pre-Kafka-retention gap, and a
    /// full on-disk fallback for any environment whose Kafka topic has no data at all.
    /// Unchanged historical behavior.
    /// </summary>
    Both,

    /// <summary>
    /// On-disk metrics log files only — the Kafka metrics consumer and disk backfill never
    /// run. Use when Kafka metrics publishing isn't wired up, or to rule it out for debugging.
    /// </summary>
    FileSystem,

    /// <summary>
    /// Kafka only, including its one-time disk backfill for the pre-retention gap — but no
    /// ongoing file-tail polling, even for services/environments where Kafka has no data.
    /// </summary>
    Kafka,
}
