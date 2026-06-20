namespace BatchMonitor.Models;

public enum TabType
{
    Batches,
    Services,
    Kafka,
    MongoDB,
    Logs,
    Settings,
    BatchDetail,
    Timeline,
    ServiceDetail,
    KafkaDetail,
    KafkaGroups,
    MongoDetail,
    LogDetail,
    FilterHelp
}

/// <summary>
/// Represents a single open tab in the tab bar.
/// </summary>
public class TabModel
{
    /// <summary>
    /// Unique identifier: "{type}:{entityId}:{env}" or "{type}:{env}" for L1 tabs.
    /// Settings has no environment: "settings".
    /// </summary>
    public string Id { get; init; } = string.Empty;

    public TabType Type { get; init; }

    /// <summary>Label shown in the tab header.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Environment this tab is scoped to. Null for Settings.</summary>
    public string? Environment { get; init; }

    /// <summary>Entity identifier (RunId for batch tabs, etc.). Null for L1 tabs.</summary>
    public string? EntityId { get; init; }

    /// <summary>MudBlazor icon constant string for the tab.</summary>
    public string Icon { get; init; } = MudBlazor.Icons.Material.Outlined.Dashboard;

    public bool IsActive { get; set; }

    // ── Factory helpers ──────────────────────────────────────────────────

    public static TabModel CreateBatchesDashboard(string env) => new()
    {
        Id          = $"dashboard:batches:{env}",
        Type        = TabType.Batches,
        Label       = "Batches",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.ViewList
    };

    public static TabModel CreateServicesDashboard(string env) => new()
    {
        Id          = $"dashboard:services:{env}",
        Type        = TabType.Services,
        Label       = "Services",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.Dns
    };

    public static TabModel CreateKafkaDashboard(string env) => new()
    {
        Id          = $"dashboard:kafka:{env}",
        Type        = TabType.Kafka,
        Label       = "Kafka",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.Stream
    };

    public static TabModel CreateMongoDashboard(string env) => new()
    {
        Id          = $"dashboard:mongo:{env}",
        Type        = TabType.MongoDB,
        Label       = "MongoDB",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.Storage
    };

    public static TabModel CreateLogsDashboard(string env) => new()
    {
        Id          = $"dashboard:logs:{env}",
        Type        = TabType.Logs,
        Label       = "Logs",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.Article
    };

    public static TabModel CreateSettings() => new()
    {
        Id          = "dashboard:settings",
        Type        = TabType.Settings,
        Label       = "Settings",
        Environment = null,
        Icon        = MudBlazor.Icons.Material.Outlined.Settings
    };

    public static TabModel CreateBatchDetail(string runId, string batchName, string env) => new()
    {
        Id          = $"detail:batch:{runId}:{env}",
        Type        = TabType.BatchDetail,
        Label       = batchName,
        Environment = env,
        EntityId    = runId,
        Icon        = MudBlazor.Icons.Material.Outlined.Hexagon
    };

    public static TabModel CreateTimeline(string runIds, string env) => new()
    {
        Id          = $"detail:timeline:{runIds}:{env}",
        Type        = TabType.Timeline,
        Label       = "Timeline",
        Environment = env,
        EntityId    = runIds,
        Icon        = MudBlazor.Icons.Material.Outlined.Timeline
    };

    public static TabModel CreateKafkaTopicInspector(string topicName, string env) => new()
    {
        Id          = $"detail:kafka:{topicName}:{env}",
        Type        = TabType.KafkaDetail,
        Label       = topicName.Length > 28 ? topicName[..28] + "…" : topicName,
        Environment = env,
        EntityId    = topicName,
        Icon        = MudBlazor.Icons.Material.Outlined.MoveToInbox
    };

    public static TabModel CreateKafkaGroupsDashboard(string env) => new()
    {
        Id          = $"dashboard:kafka-groups:{env}",
        Type        = TabType.KafkaGroups,
        Label       = "Consumers",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.Groups
    };

    public static TabModel CreateMongoCollectionInspector(string database, string collection, string env) => new()
    {
        Id          = $"detail:mongo:{database}:{collection}:{env}",
        Type        = TabType.MongoDetail,
        Label       = collection.Length > 24 ? collection[..24] + "…" : collection,
        Environment = env,
        EntityId    = $"{database}/{collection}",
        Icon        = MudBlazor.Icons.Material.Outlined.TableChart
    };

    public static TabModel CreateFilterHelp() => new()
    {
        Id   = "help:filter",
        Type = TabType.FilterHelp,
        Label = "Filter syntax",
        Icon  = MudBlazor.Icons.Material.Outlined.HelpOutline
    };
}
