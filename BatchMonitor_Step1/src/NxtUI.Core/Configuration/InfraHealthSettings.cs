namespace NxtUI.Core.Configuration;

/// <summary>
/// Controls how often the Home page's Kafka/Mongo status strip polls each service, and
/// when a run of failed pings escalates from "Degraded" (yellow — occasional/intermittent,
/// might just be a blip) to "Down" (red — consistently unreachable).
/// </summary>
public class InfraHealthSettings
{
    public const string SectionName = "InfraHealth";

    /// <summary>How often (seconds) to ping Kafka's cluster metadata. Default: 30.</summary>
    public int KafkaPollIntervalSeconds { get; set; } = 30;

    /// <summary>How often (seconds) to ping Mongo (a lightweight {ping:1} on "admin"). Default: 30.</summary>
    public int MongoPollIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Max seconds a single Mongo ping is allowed to take before it's treated as a
    /// failure — kept short deliberately so a genuinely unreachable server doesn't tie up
    /// an entire poll cycle waiting out the driver's own (much longer) default timeout.
    /// Default: 5.
    /// </summary>
    public int MongoPingTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Consecutive failed polls (for the same service) required before its status escalates
    /// from Degraded (yellow — "unreachable just now, might be a blip") to Down (red —
    /// "consistently unreachable"). A single isolated failure after a run of successes
    /// always shows Degraded, never Down. Default: 3.
    /// </summary>
    public int ConsecutiveFailuresBeforeDown { get; set; } = 3;
}
