using BatchMonitor.Configuration;
using BatchMonitor.Core.Services;
using BatchMonitor.Hubs;
using BatchMonitor.Services;
using Microsoft.AspNetCore.SignalR;
using MudBlazor.Services;

namespace BatchMonitor;

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

        // ── Blazor + MudBlazor ───────────────────────────────────────────────
        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();

        // register controllers (API endpoints)
        builder.Services.AddControllers();

        builder.Services.AddMudServices();

        // ── SignalR ──────────────────────────────────────────────────────────
        builder.Services.AddSignalR();

        // ── Batch service ─────────────────────────────────────────────────────
        // Use MockBatchService for development / demo without a MongoDB instance.
        // Swap to MongoBatchService when connecting to a real cluster:
        //   builder.Services.AddSingleton<IBatchService, MongoBatchService>();
        builder.Services.AddSingleton<IBatchService, MockBatchService>(sp =>
            new MockBatchService(sp.GetRequiredService<IHubContext<BatchEventsHub>>()));

        builder.Services.AddSingleton<IKafkaService, MockKafkaService>();
        builder.Services.AddSingleton<IMongoService, MockMongoService>();
        builder.Services.AddSingleton<IInfraHealthService, MockInfraHealthService>();
        // Swap to MongoHeartbeatService when connecting to a real cluster:
        //   builder.Services.AddSingleton<IHeartbeatService, MongoHeartbeatService>();
        builder.Services.AddSingleton<IHeartbeatService, MockHeartbeatService>();

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

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();

        // map controllers
        app.MapControllers();

        app.MapBlazorHub();
        app.MapHub<BatchHub>("/hubs/batch");
        app.MapHub<BatchEventsHub>("/hubs/batch-events");
        app.MapFallbackToPage("/_Host");

        app.Run();
    }
}
