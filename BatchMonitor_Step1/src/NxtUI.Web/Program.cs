using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using NLog;
using NLog.Web;
using NxtUI.Configuration;
using NxtUI.Core.Services;
using NxtUI.Core.Services.Kafka;
using NxtUI.Core.Services.Mock;
using NxtUI.Core.Services.Mongo;
using NxtUI.Core.Services.Sql;
using NxtUI.Protos;
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

        builder.Services.Configure<RunsSettings>(
            builder.Configuration.GetSection(RunsSettings.SectionName));

        // ── Per-environment config loader ─────────────────────────────────────
        builder.Services.AddSingleton(new EnvironmentConfigOptions
        {
            BasePath = Path.Combine(builder.Environment.ContentRootPath, "config")
        });
        builder.Services.AddSingleton<EnvironmentConfigLoader>();

        // ── Connection factories (shared client when settings fingerprint matches) ──
        builder.Services.AddSingleton<KafkaConnectionFactory>();
        builder.Services.AddSingleton<MongoConnectionFactory>();

        // ── Proto message registry + deserialization pipeline ─────────────────
        // .proto files are parsed dynamically at runtime (no generated C# classes);
        // schema is cached for the process lifetime — restart to pick up edits.
        builder.Services.AddSingleton<IMessageRegistry>(sp =>
            MessageRegistry.Create(sp.GetRequiredService<IOptions<KafkaSettings>>().Value.ProtoSchemaFolder));
        builder.Services.AddSingleton<TopicDeserializerPipeline>();

        // ── Blazor + MudBlazor ───────────────────────────────────────────────
        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();

        // register controllers (API endpoints)
        builder.Services.AddControllers();

        builder.Services.AddMudServices();

        // ── Operation tracker (singleton — used by background services + diagnostics monitor) ──
        builder.Services.AddSingleton<OperationTracker>();

        // ── SignalR ──────────────────────────────────────────────────────────
        // BlazorCircuitFilter is conditionally added below after config is read.
        var signalRBuilder = builder.Services.AddSignalR();

        // Always on (unlike BlazorCircuitFilter): reports unhandled UI-event-handler
        // exceptions as a toast + Settings history entry instead of crashing the circuit.
        builder.Services.AddScoped<ErrorNotificationHubFilter>();
        signalRBuilder.AddHubOptions<Microsoft.AspNetCore.SignalR.Hub>(o =>
            o.AddFilter<ErrorNotificationHubFilter>());

        // ── Batch / Run service ──────────────────────────────────────────────
        // Mock: fast demo without any backend.  Swap comment for a real backend.
        builder.Services.AddSingleton<IRunService, MockRunService>(sp =>
            new MockRunService(
                sp.GetRequiredService<IHubContext<RunEventsHub>>(),
                sp.GetRequiredService<IOptions<RunsSettings>>().Value));
        // builder.Services.AddSingleton<IRunService>(sp => new MongoRunService(
        //     sp.GetRequiredService<MongoConnectionFactory>(),
        //     sp.GetRequiredService<IOptions<MongoSettings>>(),
        //     sp.GetRequiredService<IOptions<RunsSettings>>().Value));
        // builder.Services.AddSingleton<IRunService>(sp => new SqlRunService(
        //     sp.GetRequiredService<EnvironmentConfigLoader>(),
        //     sp.GetRequiredService<IOptions<RunsSettings>>().Value,
        //     sp.GetRequiredService<ILogger<SqlRunService>>()));

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

        if (builder.Configuration.GetValue<bool>("Diagnostics:Enabled"))
        {
            builder.Services.AddHostedService<ServerDiagnosticsMonitor>();
            builder.Services.AddSingleton<BlazorCircuitFilter>();
            signalRBuilder.AddHubOptions<Microsoft.AspNetCore.SignalR.Hub>(o =>
                o.AddFilter<BlazorCircuitFilter>());
        }

        // ── Application services (Scoped = one per Blazor circuit/session) ───
        builder.Services.AddScoped<TabService>();
        builder.Services.AddScoped<EnvironmentSelectorService>();
        builder.Services.AddScoped<SignalRConnectionService>();
        builder.Services.AddScoped<ThemeService>();
        builder.Services.AddScoped<DateTimeDisplayService>();
        builder.Services.AddScoped<ErrorNotificationService>();

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

        app.MapRazorPages();
        app.MapBlazorHub();
        app.MapHub<RunHub>("/hubs/run");
        app.MapHub<RunEventsHub>("/hubs/run-events");

        // Lightweight JSON endpoint for the home treemap — JS polls this directly
        // so the data never travels over SignalR.
        app.MapGet("/api/treemap/{env}", (
            string env,
            HeartbeatMonitor        heartbeat,
            ServiceMetricsMonitor   metrics) =>
        {
            var services = heartbeat.GetServices(env);
            if (services is null) return Results.Ok(new { name = "root", children = Array.Empty<object>() });

            // Include all services the heartbeat knows about (online and offline).
            // The heartbeat service already manages freshness — no extra cutoff here.
            var hosts = services
                .GroupBy(s => s.HostName)
                .Select(g => new
                {
                    name     = g.Key,
                    children = g.Select(s =>
                    {
                        var m = metrics.GetLatest(env, s);
                        return new
                        {
                            name   = s.ServiceName,
                            pid    = s.ProcessId,
                            online = s.IsOnline,
                            ram    = m is not null ? (double?)((m.CurrentUsageBytes + m.ChildUsageBytes) / 1_048_576.0) : null,
                        };
                    })
                    .OrderByDescending(s => s.ram ?? 0)
                    .ToArray()
                })
                .ToArray();

            return Results.Ok(new { name = "root", children = hosts });
        });

        app.MapFallbackToPage("/{**path}", "/_Host");

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

        string consoleLayout =
            T  + "${date:format=HH\\:mm\\:ss.fff}" + R + " "
          + TH + "${threadid:padding=3}" + R + " "
          + levelColor + "${pad:padding=-5:inner=${level:uppercase=true}}" + R + " "
          + "${message} ${exception:format=tostring} "
          + C  + "${callsite:className=true:methodName=true:fileName=false}" + R;

        var console = new NLog.Targets.ConsoleTarget("console") { Layout = consoleLayout };

        // ── File target — per-class files, one folder per process run ─────────
        // Stamp GDC once so the folder name is fixed for the lifetime of this process.
        var startStamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var pid        = System.Diagnostics.Process.GetCurrentProcess().Id;
        NLog.GlobalDiagnosticsContext.Set("startStamp", startStamp);
        NLog.GlobalDiagnosticsContext.Set("pid",        pid.ToString());

        // Format: datetime_utc | threadid | level | message | exception | callsite
        const string fileLayout =
            "${date:format=yyyy-MM-dd HH\\:mm\\:ss.fff:universalTime=true}"
          + " | ${threadid:padding=4}"
          + " | ${pad:padding=-5:inner=${level:uppercase=true}}"
          + " | ${message} ${exception:format=tostring}"
          + " | ${callsite:className=true:methodName=true:fileName=false}";

        var file = new NLog.Targets.FileTarget("file")
        {
            FileName         = @"C:\temp\logs\NxtUI_${gdc:item=startStamp}_PID_${gdc:item=pid}\${logger:shortName=true}.log",
            Layout           = fileLayout,
            KeepFileOpen     = true,
            ConcurrentWrites = false,
            Encoding         = System.Text.Encoding.UTF8,
            CreateDirs       = true,
        };

        var cfg = new NLog.Config.LoggingConfiguration();
        cfg.AddTarget(console);
        cfg.AddTarget(file);

        // ── Console rules ─────────────────────────────────────────────────────
        cfg.AddRule(NLog.LogLevel.Warn,  NLog.LogLevel.Fatal, console, "Microsoft.*",                         true);
        cfg.AddRule(NLog.LogLevel.Warn,  NLog.LogLevel.Fatal, console, "System.*",                            true);
        cfg.AddRule(NLog.LogLevel.Warn,  NLog.LogLevel.Fatal, console, "Microsoft.Extensions.Localization.*", true);
        cfg.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, console, "NxtUI.*");
        cfg.AddRule(NLog.LogLevel.Info,  NLog.LogLevel.Fatal, console);

        // ── File rules (NxtUI app code + ASP.NET warnings) ───────────────────
        cfg.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, file, "NxtUI.*");
        cfg.AddRule(NLog.LogLevel.Warn,  NLog.LogLevel.Fatal, file, "Microsoft.AspNetCore.*");
        cfg.AddRule(NLog.LogLevel.Warn,  NLog.LogLevel.Fatal, file, "Microsoft.Hosting.*");

        LogManager.Configuration = cfg;
    }
}
