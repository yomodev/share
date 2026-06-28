using NxtUI.Configuration;
using NxtUI.Core.Services;
using NxtUI.Core.Services.Mock;
using NxtUI.Core.Services.Mongo;
using NxtUI.Hubs;
using NxtUI.Services;
using NxtUI.Web.Services;
using Microsoft.AspNetCore.SignalR;
using MudBlazor.Services;

namespace NxtUI;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

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

        // ── Blazor + MudBlazor ───────────────────────────────────────────────
        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();

        // register controllers (API endpoints)
        builder.Services.AddControllers();

        builder.Services.AddMudServices();

        // ── SignalR ──────────────────────────────────────────────────────────
        builder.Services.AddSignalR();

        // ── Batch service ─────────────────────────────────────────────────────
        // Use MockRunService for development / demo without a MongoDB instance.
        // Swap to MongoRunService when connecting to a real cluster:
        //   builder.Services.AddSingleton<IRunService, MongoRunService>();
        builder.Services.AddSingleton<IRunService, MockRunService>(sp =>
            new MockRunService(sp.GetRequiredService<IHubContext<RunEventsHub>>()));

        builder.Services.AddSingleton<IKafkaService, MockKafkaService>();
        builder.Services.AddSingleton<IMongoService, MockMongoService>();
        builder.Services.AddSingleton<IInfraHealthService, MockInfraHealthService>();
        // Swap to MongoHeartbeatService when connecting to a real cluster:
        //   builder.Services.AddSingleton<IHeartbeatService, MongoHeartbeatService>();
        builder.Services.AddSingleton<IHeartbeatService, MockHeartbeatService>();
        builder.Services.AddSingleton<IEnvironmentService, MockEnvironmentService>();
        builder.Services.AddSingleton<ILogPathDiscoveryService, LogPathDiscoveryService>();
        builder.Services.AddSingleton<ILogBrowserService, LogBrowserService>();
        builder.Services.AddSingleton<ILogViewerService, LogViewerService>();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IBatchCatalogService, NxtUI.Web.Services.BatchCatalogService>();

        // Metrics monitor — single instance shared between DI consumers and the
        // hosted background loop (so subscriptions and the poller see the same state).
        builder.Services.AddSingleton<ServiceMetricsMonitor>();
        builder.Services.AddSingleton<IServiceMetricsMonitor>(sp => sp.GetRequiredService<ServiceMetricsMonitor>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceMetricsMonitor>());

        // Test-only: fabricates metrics log files (no-op unless TestLogGenerator:Enabled).
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
}
