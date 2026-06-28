namespace NxtUI.Web.Models;

public enum TabType
{
    Runs,
    Services,
    Pipelines,
    Kafka,
    MongoDB,
    Config,
    Batches,
    LogBrowser,
    Settings,
    RunDetail,
    Timeline,
    ServiceDetail,
    KafkaMessages,
    KafkaGroups,
    MongoDetail,
    LogViewer,
    LogWorkspace,
    FilterHelp,
    MemoryGraph
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

    /// <summary>
    /// Optional mutable navigation hint — updated on an already-open tab without changing
    /// its identity. Used e.g. by LogBrowser to navigate to a specific folder when opened
    /// from the Services page.
    /// </summary>
    public string? NavigationHint { get; set; }

    /// <summary>MudBlazor icon constant string for the tab.</summary>
    public string Icon { get; set; } = MudBlazor.Icons.Material.Outlined.Dashboard;

    public bool IsActive { get; set; }

    /// <summary>When true, opening this tab does not trigger URL navigation (e.g. browser-local files).</summary>
    public bool NoNavigate { get; set; }

    // ── Factory helpers ──────────────────────────────────────────────────

    public static TabModel CreateRunsDashboard(string env) => new()
    {
        Id          = $"dashboard:runs:{env}",
        Type        = TabType.Runs,
        Label       = "Runs",
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

    public static TabModel CreatePipelinesDashboard(string env) => new()
    {
        Id          = $"dashboard:pipelines:{env}",
        Type        = TabType.Pipelines,
        Label       = "Pipelines",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.AccountTree
    };

    public static TabModel CreateConfigDashboard(string env) => new()
    {
        Id          = $"dashboard:config:{env}",
        Type        = TabType.Config,
        Label       = "Config",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.Tune
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

    public static TabModel CreateMemoryGraph(string env) => new()
    {
        Id          = $"dashboard:memory:{env}",
        Type        = TabType.MemoryGraph,
        Label       = "Memory",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.Memory
    };

    public static TabModel CreateBatchesDashboard(string env) => new()
    {
        Id          = $"dashboard:batches:{env}",
        Type        = TabType.Batches,
        Label       = "Batches",
        Environment = env,
        Icon        = MudBlazor.Icons.Material.Outlined.ViewList
    };

    public static TabModel CreateLogsDashboard(string env) => new()
    {
        Id          = $"dashboard:logs:{env}",
        Type        = TabType.LogBrowser,
        Label       = "Log Browser",
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

    public static TabModel CreateRunDetail(string runId, string Name, string env) => new()
    {
        Id          = $"detail:run:{runId}:{env}",
        Type        = TabType.RunDetail,
        Label       = Name,
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
        Type        = TabType.KafkaMessages,
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

    public static TabModel CreateLogViewer(string path, string env) => new()
    {
        Id          = $"detail:log:{Uri.EscapeDataString(path)}:{env}",
        Type        = TabType.LogViewer,
        Label       = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : "Log",
        Environment = env,
        EntityId    = path,
        Icon        = MudBlazor.Icons.Material.Outlined.Article
    };

    public static TabModel CreateLogWorkspace(string path, string env) => new()
    {
        Id          = $"detail:workspace:{Uri.EscapeDataString(path)}:{env}",
        Type        = TabType.LogWorkspace,
        Label       = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : "Log",
        Environment = env,
        EntityId    = path,
        Icon        = MudBlazor.Icons.Material.Outlined.Article
    };

    public static TabModel CreateFilterHelp() => new()
    {
        Id   = "help:filter",
        Type = TabType.FilterHelp,
        Label = "Filter syntax",
        Icon  = MudBlazor.Icons.Material.Outlined.HelpOutline
    };

    // ── URL mapping ──────────────────────────────────────────────────────

    /// <summary>Returns the canonical URL path for this tab.</summary>
    public string GetUrl() => Type switch
    {
        TabType.Runs     => $"/runs/{Environment}",
        TabType.Services    => $"/services/{Environment}",
        TabType.Pipelines   => $"/pipelines/{Environment}",
        TabType.Kafka       => $"/kafka/{Environment}",
        TabType.KafkaGroups => $"/kafka/{Environment}",
        TabType.MongoDB     => $"/mongo/{Environment}",
        TabType.Config      => $"/config/{Environment}",
        TabType.Batches     => $"/batches/{Environment}",
        TabType.MemoryGraph => $"/memory/{Environment}",
        TabType.LogBrowser        => $"/logs/{Environment}",
        TabType.LogViewer    => $"/log/{Environment}/{Uri.EscapeDataString(EntityId ?? "")}",
        TabType.LogWorkspace => $"/workspace/{Environment}/{Uri.EscapeDataString(EntityId ?? "")}",
        TabType.Settings    => "/settings",
        TabType.FilterHelp  => "/help/filter",
        TabType.RunDetail => $"/run/{Environment}/{Uri.EscapeDataString(EntityId ?? "")}",
        TabType.Timeline    => $"/timeline/{Environment}/{Uri.EscapeDataString(EntityId ?? "")}",
        TabType.KafkaMessages => $"/kafka/{Environment}/topic/{Uri.EscapeDataString(EntityId ?? "")}",
        TabType.MongoDetail => BuildMongoDetailUrl(),
        _                   => "/"
    };

    private string BuildMongoDetailUrl()
    {
        // EntityId is "database/collection" — split and encode each segment separately
        var parts = (EntityId ?? "").Split('/', 2);
        var db  = Uri.EscapeDataString(parts.Length > 0 ? parts[0] : "");
        var col = Uri.EscapeDataString(parts.Length > 1 ? parts[1] : "");
        return $"/mongo/{Environment}/{db}/{col}";
    }

    /// <summary>
    /// Parses a URL path and returns the matching TabModel, or null if unrecognised.
    /// The tab is created with default label/icon — the host page updates these once data loads.
    /// </summary>
    public static TabModel? FromUrl(string path)
    {
        var parts = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        return parts[0].ToLowerInvariant() switch
        {
            "runs" when parts.Length >= 2
                => CreateRunsDashboard(parts[1]),

            "services" when parts.Length >= 2
                => CreateServicesDashboard(parts[1]),

            "pipelines" when parts.Length >= 2
                => CreatePipelinesDashboard(parts[1]),

            "kafka" when parts.Length == 2
                => CreateKafkaDashboard(parts[1]),

            "kafka" when parts.Length >= 4 && parts[2].Equals("topic", StringComparison.OrdinalIgnoreCase)
                => CreateKafkaTopicInspector(Uri.UnescapeDataString(parts[3]), parts[1]),

            "mongo" when parts.Length == 2
                => CreateMongoDashboard(parts[1]),

            "mongo" when parts.Length >= 4
                => CreateMongoCollectionInspector(
                       Uri.UnescapeDataString(parts[2]),
                       Uri.UnescapeDataString(parts[3]),
                       parts[1]),

            "run" when parts.Length >= 3
                => CreateRunDetail(Uri.UnescapeDataString(parts[2]), Uri.UnescapeDataString(parts[2]), parts[1]),

            "timeline" when parts.Length >= 3
                => CreateTimeline(Uri.UnescapeDataString(parts[2]), parts[1]),

            "config" when parts.Length >= 2
                => CreateConfigDashboard(parts[1]),

            "batches" when parts.Length >= 2
                => CreateBatchesDashboard(parts[1]),

            "memory" when parts.Length >= 2
                => CreateMemoryGraph(parts[1]),

            "logs" when parts.Length >= 2
                => CreateLogsDashboard(parts[1]),

            "log" when parts.Length >= 3
                => CreateLogViewer(Uri.UnescapeDataString(parts[2]), parts[1]),

            "workspace" when parts.Length >= 3
                => CreateLogWorkspace(Uri.UnescapeDataString(parts[2]), parts[1]),

            "settings"
                => CreateSettings(),

            "help" when parts.Length >= 2 && parts[1].Equals("filter", StringComparison.OrdinalIgnoreCase)
                => CreateFilterHelp(),

            _ => null
        };
    }
}
