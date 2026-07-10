namespace NxtUI.Core.Models;

/// <summary>
/// Health status for an infra dependency (Kafka/Mongo). <see cref="Down"/> is the enum's
/// default (value 0) — deliberately, so a record's default-initialized <c>Status</c>
/// (before any poll has ever completed for that environment) reads as Down/red rather
/// than a separate ambiguous "Unknown" state.
/// </summary>
public enum HealthStatus
{
    /// <summary>
    /// Default state before the first check ever completes, AND the state once consecutive
    /// failures reach <see cref="Configuration.InfraHealthSettings.ConsecutiveFailuresBeforeDown"/>.
    /// Red.
    /// </summary>
    Down,

    /// <summary>A single check just failed (timeout or error) — not yet enough in a row to
    /// escalate toward Down. Yellow, steady (not blinking).</summary>
    Degraded,

    /// <summary>Two or more consecutive failures, still short of the Down threshold — warns
    /// that status is trending toward Down soon. Yellow, blinking.</summary>
    DegradedEscalating,

    /// <summary>Last check succeeded. Green.</summary>
    Healthy,
}

public record BrokerHealth
{
    public int Id { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public bool IsOnline { get; init; }
    public int? LatencyMs { get; init; }
}

public record KafkaHealth
{
    public HealthStatus Status { get; init; }
    public IReadOnlyList<BrokerHealth> Brokers { get; init; } = [];
    public DateTime CheckedAt { get; init; }
    public string? Error { get; init; }
}

public record MongoNodeHealth
{
    public string Host { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public int? LatencyMs { get; init; }
}

public record MongoHealth
{
    public HealthStatus Status { get; init; }
    public int DatabaseCount { get; init; }
    public IReadOnlyList<MongoNodeHealth> Nodes { get; init; } = [];
    public DateTime CheckedAt { get; init; }
    public string? Error { get; init; }
}
