using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Services;
using NxtUI.Core.Services.Kafka;
using NxtUI.Core.Services.Mock;
using NxtUI.Core.Services.Mongo;
using NxtUI.Web.Services;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Shared xUnit class fixture that boots the same DI container as Program.cs
/// (minus Blazor / MudBlazor / HTTP pipeline) so perf tests exercise the
/// real service call paths.
///
/// By default the mock implementations are registered — same as the running app.
/// To test against real infrastructure, swap the commented lines in RegisterServices().
/// </summary>
public sealed class ServiceFixture : IAsyncLifetime
{
    private IHost _host = null!;

    public IServiceProvider Services => _host.Services;

    /// <summary>Environment IDs from App.Environments in appsettings.json.</summary>
    public string[] Environments { get; private set; } = [];

    /// <summary>The default environment (App.DefaultEnvironment).</summary>
    public string DefaultEnv { get; private set; } = "DEV1";

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async ValueTask InitializeAsync()
    {
        var config = BuildConfiguration();

        var appSettings = config.GetSection(AppSettings.SectionName).Get<AppSettings>() ?? new();
        Environments = appSettings.Environments.Select(e => e.Id).ToArray();
        DefaultEnv   = appSettings.DefaultEnvironment;

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(log =>
            {
                log.ClearProviders();
                // Uncomment to see debug output from services in test output:
                // log.AddConsole().SetMinimumLevel(LogLevel.Debug);
            })
            .ConfigureServices((_, svc) => RegisterServices(svc, config))
            .Build();

        await _host.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    // ── private ───────────────────────────────────────────────────────────────

    // Walk up from the test output directory to find NxtUI.Web's own folder.
    // Typical path: tests/NxtUI.Tests/bin/Debug/net8.0 → src/NxtUI.Web
    private static string? FindWebRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "src", "NxtUI.Web", "appsettings.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        return null;
    }

    private static IConfiguration BuildConfiguration()
    {
        var webRoot = FindWebRoot();
        if (webRoot is not null)
            return new ConfigurationBuilder()
                .SetBasePath(webRoot)
                .AddJsonFile("appsettings.json")
                .AddJsonFile("appsettings.Development.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

        // Fallback: minimal in-memory config so tests can still run without the file.
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:DefaultEnvironment"]       = "DEV1",
                ["App:Environments:0:Id"]        = "DEV1",
                ["App:Environments:0:Label"]     = "Development 1",
                ["App:Environments:0:Tier"]      = "Development",
                ["Heartbeats:IntervalSeconds"]   = "30",
                ["Heartbeats:IdleReleaseMinutes"]= "10",
                ["Logs:MetricsIntervalSeconds"]  = "90",
                ["Logs:IdleReleaseMinutes"]      = "10",
                ["Mongo:ConnectionString"]       = "mongodb://localhost:27017",
                ["Kafka:BootstrapServers"]       = "localhost:9092",
            })
            .Build();
    }

    private static void RegisterServices(IServiceCollection svc, IConfiguration config)
    {
        // ── Configuration ─────────────────────────────────────────────────────
        svc.Configure<AppSettings>      (config.GetSection(AppSettings.SectionName));
        svc.Configure<HeartbeatSettings>(config.GetSection("Heartbeats"));
        svc.Configure<LogPathSettings>  (config.GetSection(LogPathSettings.SectionName));
        svc.Configure<MongoSettings>    (config.GetSection(MongoSettings.SectionName));
        svc.Configure<KafkaSettings>    (config.GetSection(KafkaSettings.SectionName));

        // ── Connection factories (used by real services) ───────────────────────
        svc.AddSingleton(new EnvironmentConfigOptions
        {
            BasePath = Path.Combine(FindWebRoot() ?? AppContext.BaseDirectory, "config")
        });
        svc.AddSingleton<EnvironmentConfigLoader>();
        svc.AddSingleton<MongoConnectionFactory>();
        svc.AddSingleton<KafkaConnectionFactory>();

        // ── Heartbeat ─────────────────────────────────────────────────────────
        // Mock (default — same as running app):
        svc.AddSingleton<IHeartbeatService, MockHeartbeatService>();
        // Real MongoDB heartbeat — swap comment to test real infrastructure:
        // svc.AddSingleton<IHeartbeatService, MongoHeartbeatService>();

        svc.AddSingleton<OperationTracker>();
        svc.AddSingleton<HeartbeatMonitor>();
        svc.AddSingleton<IHeartbeatMonitor>(sp => sp.GetRequiredService<HeartbeatMonitor>());
        svc.AddHostedService(sp => sp.GetRequiredService<HeartbeatMonitor>());

        // ── Mongo ─────────────────────────────────────────────────────────────
        // Mock (default):
        svc.AddSingleton<MockMongoService>();
        svc.AddSingleton<IMongoService>(sp => sp.GetRequiredService<MockMongoService>());
        svc.AddSingleton<IMongoReader> (sp => sp.GetRequiredService<MockMongoService>());
        svc.AddSingleton<IMongoAdmin>  (sp => sp.GetRequiredService<MockMongoService>());
        // Real MongoDB — swap comment block:
        // svc.AddSingleton<MongoService>();
        // svc.AddSingleton<IMongoService>(sp => sp.GetRequiredService<MongoService>());
        // svc.AddSingleton<IMongoReader> (sp => sp.GetRequiredService<MongoService>());
        // svc.AddSingleton<IMongoAdmin>  (sp => sp.GetRequiredService<MongoService>());

        // ── Kafka ─────────────────────────────────────────────────────────────
        // Mock (default):
        svc.AddSingleton<MockKafkaService>();
        svc.AddSingleton<IKafkaService>(sp => sp.GetRequiredService<MockKafkaService>());
        svc.AddSingleton<IKafkaMonitor>(sp => sp.GetRequiredService<MockKafkaService>());
        svc.AddSingleton<IKafkaAdmin>  (sp => sp.GetRequiredService<MockKafkaService>());
        // Real Kafka broker — swap comment block:
        // svc.AddSingleton<KafkaService>();
        // svc.AddSingleton<IKafkaService>(sp => sp.GetRequiredService<KafkaService>());
        // svc.AddSingleton<IKafkaMonitor>(sp => sp.GetRequiredService<KafkaService>());
        // svc.AddSingleton<IKafkaAdmin>  (sp => sp.GetRequiredService<KafkaService>());

        // ── Log discovery + browser ───────────────────────────────────────────
        svc.AddSingleton<IEnvironmentService, MockEnvironmentService>();
        svc.AddSingleton<ILogPathDiscoveryService, LogPathDiscoveryService>();
        svc.AddSingleton<ILogBrowserService, LogBrowserService>();
        svc.AddHttpClient();

        // ── Metrics monitor ───────────────────────────────────────────────────
        svc.AddSingleton<ServiceMetricsMonitor>();
        svc.AddSingleton<IServiceMetricsMonitor>(sp => sp.GetRequiredService<ServiceMetricsMonitor>());
        svc.AddHostedService(sp => sp.GetRequiredService<ServiceMetricsMonitor>());
    }
}
