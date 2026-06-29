namespace NxtUI.Configuration;

public class HeartbeatSettings
{
    public const string SectionName = "Heartbeats";

    /// <summary>MongoDB collection name for heartbeat documents.</summary>
    public string CollectionName { get; set; } = "Heartbeats";

    /// <summary>
    /// Expected interval (in seconds) at which services write heartbeats.
    /// A service is considered offline if its last UpdatedDateTime is older than
    /// IntervalSeconds * 2.
    /// </summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Minutes of inactivity (no subscribers) after which cached service data is released from memory.
    /// </summary>
    public int IdleReleaseMinutes { get; set; } = 10;
}
