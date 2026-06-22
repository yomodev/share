namespace NxtUI.Core.Models;

public enum HealthStatus { Unknown, Checking, Healthy, Degraded, Down }

public record BrokerHealth
{
    public int    Id        { get; init; }
    public string Host      { get; init; } = string.Empty;
    public int    Port      { get; init; }
    public bool   IsOnline  { get; init; }
    public int?   LatencyMs { get; init; }
}

public record KafkaHealth
{
    public HealthStatus              Status    { get; init; } = HealthStatus.Unknown;
    public IReadOnlyList<BrokerHealth> Brokers { get; init; } = [];
    public DateTime                  CheckedAt { get; init; }
    public string?                   Error     { get; init; }
}

public record MongoNodeHealth
{
    public string Host      { get; init; } = string.Empty;
    public string Role      { get; init; } = string.Empty;
    public bool   IsOnline  { get; init; }
    public int?   LatencyMs { get; init; }
}

public record MongoHealth
{
    public HealthStatus                Status        { get; init; } = HealthStatus.Unknown;
    public int                         DatabaseCount { get; init; }
    public IReadOnlyList<MongoNodeHealth> Nodes      { get; init; } = [];
    public DateTime                    CheckedAt     { get; init; }
    public string?                     Error         { get; init; }
}
