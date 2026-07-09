namespace NxtUI.Configuration;

public class HeartbeatSettings
{
    public const string SectionName = "Heartbeats";

    /// <summary>MongoDB collection name for heartbeat documents.</summary>
    public string CollectionName { get; set; } = "Heartbeats";

    /// <summary>
    /// Expected interval (in seconds) at which services write heartbeats.
    /// </summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Seconds since a service's last heartbeat after which it's considered offline.
    /// 0 (the default) falls back to IntervalSeconds * 2.
    /// </summary>
    public int OfflineThresholdSeconds { get; set; } = 0;

    /// <summary>Resolved offline threshold, applying the IntervalSeconds * 2 fallback.</summary>
    public TimeSpan OfflineThreshold => TimeSpan.FromSeconds(OfflineThresholdSeconds > 0
        ? OfflineThresholdSeconds
        : IntervalSeconds * 2);

    /// <summary>
    /// Only services with a heartbeat within this many minutes are fetched from MongoDB.
    /// Matches the default filter shown on the services page ("updated:>-Nm").
    /// </summary>
    public int RecentWindowMinutes { get; set; } = 30;

    /// <summary>
    /// Minutes of inactivity (no subscribers) after which cached service data is released from memory.
    /// </summary>
    public int IdleReleaseMinutes { get; set; } = 10;
}
