using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using NLog.Web;
using NxtUI.Configuration;
using NxtUI.Core.Configuration;
using NxtUI.Core.Services;
using NxtUI.Core.Services.Kafka;
using NxtUI.Core.Services.Mock;
using NxtUI.Core.Services.Mongo;
using NxtUI.Protos;
using NxtUI.Web.Hubs;
using NxtUI.Web.Services;

namespace NxtUI.Web;

public class Program
{
    public static void Main(string[] args)
    {
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

        builder.Services.Configure<UiSettings>(
            builder.Configuration.GetSection(UiSettings.SectionName));

        builder.Services.Configure<EnvConfigSettings>(
            builder.Configuration.GetSection(EnvConfigSettings.SectionName));

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
        builder.Services.AddSingleton<RunEventBroker>();

        // Mock: fast demo without any backend.  Swap comment for a real backend.
        builder.Services.AddSingleton<IRunService, MockRunService>(sp =>
            new MockRunService(
                sp.GetRequiredService<RunEventBroker>(),
                sp.GetRequiredService<IOptions<RunsSettings>>().Value));
        // builder.Services.AddSingleton<IRunService>(sp => new MongoRunService(
        //     sp.GetRequiredService<MongoConnectionFactory>(),
        //     sp.GetRequiredService<IOptions<MongoSettings>>(),
        //     sp.GetRequiredService<IOptions<RunsSettings>>().Value));
        // builder.Services.AddSingleton<IRunService>(sp => new SqlRunService(
        //     sp.GetRequiredService<EnvironmentConfigLoader>(),
        //     sp.GetRequiredService<IOptions<RunsSettings>>().Value,
        //     sp.GetRequiredService<MongoConnectionFactory>(),
        //     sp.GetRequiredService<IOptions<MongoSettings>>(),
        //     sp.GetRequiredService<ILogger<SqlRunService>>()));

        // Shares one poll loop across all circuits watching the same run, for
        // backends that don't push events themselves (skips MockRunService — see
        // IPushesOwnRunEvents).
        builder.Services.AddHostedService<RunEventWatcher>();

        // ── Kafka ─────────────────────────────────────────────────────────────
        // Mock: no broker required.  Real: swap comment block below.
        builder.Services.AddSingleton<MockKafkaService>();
        builder.Services.AddSingleton<IKafkaService>(sp => sp.GetRequiredService<MockKafkaService>());
        builder.Services.AddSingleton<IKafkaMonitor>(sp => sp.GetRequiredService<MockKafkaService>());
        builder.Services.AddSingleton<IKafkaAdmin>(sp => sp.GetRequiredService<MockKafkaService>());
        // builder.Services.AddSingleton<KafkaService>();
        // builder.Services.AddSingleton<IKafkaService> (sp => sp.GetRequiredService<KafkaService>());
        // builder.Services.AddSingleton<IKafkaMonitor> (sp => sp.GetRequiredService<KafkaService>());
        // builder.Services.AddSingleton<IKafkaAdmin>   (sp => sp.GetRequiredService<KafkaService>());

        // ── MongoDB collections ───────────────────────────────────────────────
        // Mock: no MongoDB required.  Real: swap comment block below.
        builder.Services.AddSingleton<MockMongoService>();
        builder.Services.AddSingleton<IMongoService>(sp => sp.GetRequiredService<MockMongoService>());
        builder.Services.AddSingleton<IMongoReader>(sp => sp.GetRequiredService<MockMongoService>());
        builder.Services.AddSingleton<IMongoAdmin>(sp => sp.GetRequiredService<MockMongoService>());
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
        builder.Services.AddSingleton<IEnvConfigService, EnvConfigService>();
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

        // Lightweight JSON endpoint for the home treemap — JS polls this directly
        // so the data never travels over SignalR.
        app.MapGet("/api/treemap/{env}", (
            string env,
            HeartbeatMonitor heartbeat,
            ServiceMetricsMonitor metrics) =>
        {
            var services = heartbeat.GetServices(env);
            if (services is null) return Results.Ok(new { name = "root", children = Array.Empty<object>() });

            // Include all services the heartbeat knows about (online and offline).
            // The heartbeat service already manages freshness — no extra cutoff here.
            var hosts = services
                .GroupBy(s => s.HostName)
                .Select(g => new
                {
                    name = g.Key,
                    children = g.Select(s =>
                    {
                        var m = metrics.GetLatest(env, s);
                        return new
                        {
                            name = s.ServiceName,
                            pid = s.ProcessId,
                            online = s.IsOnline,
                            ram = m is not null ? (double?)((m.CurrentUsageBytes + m.ChildUsageBytes) / 1_048_576.0) : null,
                        };
                    })
                    .OrderByDescending(s => s.ram ?? 0)
                    .ToArray()
                })
                .ToArray();

            return Results.Ok(new { name = "root", children = hosts });
        });

        // Streams a log file's raw bytes over plain HTTP instead of the server reading
        // the whole file into one C# string and shipping it as a single SignalR interop
        // payload (see LogViewer.razor / log-viewer.js's renderStream). `length` caps the
        // read to a size stat'd just before the fetch started, so the client's content and
        // the tail-poll offset it continues from afterward agree on the same cutoff.
        app.MapGet("/api/logs/read", async (
            string path,
            long? length,
            HttpResponse response,
            ILogViewerService svc,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // Fail with a real status + text/plain body instead of letting the framework's
            // Developer Exception Page (HTML, 200 in some hosting configs) mask the failure
            // and get misread by the client as if it were valid log content.
            FileStream fs;
            try
            {
                fs = svc.OpenRead(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to open log file for streaming: {Path}", path);
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
            }

            await using (fs)
            {
                response.ContentType = "text/plain; charset=utf-8";
                var remaining = length ?? fs.Length;
                if (length.HasValue) response.ContentLength = length;

                var buffer = new byte[81920];
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
                    if (read == 0) break;
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
            }
            return Results.Empty;
        });

        app.MapFallbackToPage("/{**path}", "/_Host");

        app.Run();
    }
}
