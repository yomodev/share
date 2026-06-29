using Microsoft.AspNetCore.SignalR;
using MudBlazor.Services;
using NLog;
using NLog.Web;
using NxtUI.Configuration;
using NxtUI.Core.Services;
using NxtUI.Core.Services.Kafka;
using NxtUI.Core.Services.Mock;
using NxtUI.Core.Services.Mongo;
using NxtUI.Web.Hubs;
using NxtUI.Web.Services;

namespace NxtUI.Web;

public class Program
{
    public static void Main(string[] args)
    {
        ConfigureNLog();

        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Host.UseNLog();

        // ── Configuration ────────────────────────────────────────────────────
        builder.Services.Configure<MongoSettings>(
            builder.Configuration.GetSection(MongoSettings.SectionName));

        builder.Services.Configure<AppSettings>(
            builder.Configuration.GetSection(AppSettings.SectionName));

        builder.Services.Configure<HeartbeatSettings>(
            builder.Configuration.GetSection(HeartbeatSettings.SectionName));

        builder.Services.Configure<LogPathSettings>(
            builder.Configuration.GetSection(LogPathSettings.SectionName));

        builder.Services.Configure<TestLogGeneratorSettings>(
            builder.Configuration.GetSection(TestLogGeneratorSettings.SectionName));

        builder.Services.Configure<KafkaSettings>(
            builder.Configuration.GetSection(KafkaSettings.SectionName));

        // ── Connection factories (singletons; swap services below to use these) ──
        builder.Services.AddSingleton<MongoConnection>();
        builder.Services.AddSingleton<KafkaConnection>();

        // ── Blazor + MudBlazor ───────────────────────────────────────────────
        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();

        // register controllers (API endpoints)
        builder.Services.AddControllers();

        builder.Services.AddMudServices();

        // ── SignalR ──────────────────────────────────────────────────────────
        builder.Services.AddSignalR();

        // ── Batch / Run service ──────────────────────────────────────────────
        // Mock: fast demo without MongoDB.  Real: swap comment below.
        builder.Services.AddSingleton<IRunService, MockRunService>(sp =>
            new MockRunService(sp.GetRequiredService<IHubContext<RunEventsHub>>()));
        // builder.Services.AddSingleton<IRunService, MongoRunService>();

        // ── Kafka ─────────────────────────────────────────────────────────────
        // Mock: no broker required.  Real: swap comment block below.
        builder.Services.AddSingleton<MockKafkaService>();
        builder.Services.AddSingleton<IKafkaService> (sp => sp.GetRequiredService<MockKafkaService>());
        builder.Services.AddSingleton<IKafkaMonitor> (sp => sp.GetRequiredService<MockKafkaService>());
        builder.Services.AddSingleton<IKafkaAdmin>   (sp => sp.GetRequiredService<MockKafkaService>());
        // builder.Services.AddSingleton<KafkaService>();
        // builder.Services.AddSingleton<IKafkaService> (sp => sp.GetRequiredService<KafkaService>());
        // builder.Services.AddSingleton<IKafkaMonitor> (sp => sp.GetRequiredService<KafkaService>());
        // builder.Services.AddSingleton<IKafkaAdmin>   (sp => sp.GetRequiredService<KafkaService>());

        // ── MongoDB collections ───────────────────────────────────────────────
        // Mock: no MongoDB required.  Real: swap comment block below.
        builder.Services.AddSingleton<MockMongoService>();
        builder.Services.AddSingleton<IMongoService> (sp => sp.GetRequiredService<MockMongoService>());
        builder.Services.AddSingleton<IMongoReader>  (sp => sp.GetRequiredService<MockMongoService>());
        builder.Services.AddSingleton<IMongoAdmin>   (sp => sp.GetRequiredService<MockMongoService>());
        // builder.Services.AddSingleton<MongoService>();
        // builder.Services.AddSingleton<IMongoService> (sp => sp.GetRequiredService<MongoService>());
        // builder.Services.AddSingleton<IMongoReader>  (sp => sp.GetRequiredService<MongoService>());
        // builder.Services.AddSingleton<IMongoAdmin>   (sp => sp.GetRequiredService<MongoService>());

        // ── Heartbeat ─────────────────────────────────────────────────────────
        // Mock: no MongoDB required.  Real: swap comment below.
        builder.Services.AddSingleton<IHeartbeatService, MockHeartbeatService>();
        // builder.Services.AddSingleton<IHeartbeatService, MongoHeartbeatService>();

        // HeartbeatMonitor — shared cache + background poller for heartbeat data.
        builder.Services.AddSingleton<HeartbeatMonitor>();
        builder.Services.AddSingleton<IHeartbeatMonitor>(sp => sp.GetRequiredService<HeartbeatMonitor>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatMonitor>());

        builder.Services.AddSingleton<IEnvironmentService, MockEnvironmentService>();
        builder.Services.AddSingleton<ILogPathDiscoveryService, LogPathDiscoveryService>();
        builder.Services.AddSingleton<ILogBrowserService, LogBrowserService>();
        builder.Services.AddSingleton<ILogViewerService, LogViewerService>();
        builder.Services.AddSingleton<InfraHealthCache>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<InfraHealthCache>());
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IBatchCatalogService, BatchCatalogService>();

        // Metrics monitor — single instance shared between DI consumers and the
        // hosted background loop (so subscriptions and the poller see the same state).
        builder.Services.AddSingleton<ServiceMetricsMonitor>();
        builder.Services.AddSingleton<IServiceMetricsMonitor>(sp => sp.GetRequiredService<ServiceMetricsMonitor>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceMetricsMonitor>());

        if (builder.Configuration.GetValue<bool>($"{TestLogGeneratorSettings.SectionName}:Enabled"))
            builder.Services.AddHostedService<TestLogGenerator>();

        // ── Application services (Scoped = one per Blazor circuit/session) ───
        builder.Services.AddScoped<TabService>();
        builder.Services.AddScoped<EnvironmentSelectorService>();
        builder.Services.AddScoped<SignalRConnectionService>();
        builder.Services.AddScoped<ThemeService>();

        builder.Services.AddHttpContextAccessor();

        var app = builder.Build();

        // ── Middleware pipeline ──────────────────────────────────────────────
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseRequestLocalization();
        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();

        // map controllers
        app.MapControllers();

        app.MapBlazorHub();
        app.MapHub<RunHub>("/hubs/run");
        app.MapHub<RunEventsHub>("/hubs/run-events");
        app.MapFallbackToPage("/_Host");

        app.Run();
    }

    private static void ConfigureNLog()
    {
        // ANSI escape sequences —  is the ESC character, valid in C# string literals
        const string Esc = "";
        string R  = Esc + "[0m";   // reset
        string T  = Esc + "[96m";  // bright cyan    - timestamp
        string TH = Esc + "[35m";  // magenta        - thread id
        string C  = Esc + "[93m";  // bright yellow  - caller method

        // Per-level color via NLog ${when} — the ESC byte is embedded as a C# escape
        string levelColor =
            "${when:when=level==LogLevel.Trace:inner=" + Esc + "[90m}"
          + "${when:when=level==LogLevel.Debug:inner=" + Esc + "[37m}"
          + "${when:when=level==LogLevel.Info:inner="  + Esc + "[92m}"
          + "${when:when=level==LogLevel.Warn:inner="  + Esc + "[33m}"
          + "${when:when=level==LogLevel.Error:inner=" + Esc + "[91m}"
          + "${when:when=level==LogLevel.Fatal:inner=" + Esc + "[95m}";

        string layout =
            T  + "${date:format=HH\\:mm\\:ss.fff}" + R + " "
          + TH + "${threadid:padding=3}" + R + " "
          + levelColor + "${pad:padding=-5:inner=${level:uppercase=true}}" + R + " "
          + "${message} ${exception:format=tostring} "
          + C  + "${callsite:className=true:methodName=true:fileName=false}" + R;

        var console = new NLog.Targets.ConsoleTarget("console") { Layout = layout };

        var cfg = new NLog.Config.LoggingConfiguration();
        cfg.AddTarget(console);

        // suppress noisy framework internals
        cfg.AddRule(NLog.LogLevel.Warn,  NLog.LogLevel.Fatal, console, "Microsoft.*",                         true);
        cfg.AddRule(NLog.LogLevel.Warn,  NLog.LogLevel.Fatal, console, "System.*",                            true);
        cfg.AddRule(NLog.LogLevel.Warn,  NLog.LogLevel.Fatal, console, "Microsoft.Extensions.Localization.*", true);

        // app code at Debug and above
        cfg.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, console, "NxtUI.*");

        // everything else at Info and above
        cfg.AddRule(NLog.LogLevel.Info,  NLog.LogLevel.Fatal, console);

        LogManager.Configuration = cfg;
    }
}
