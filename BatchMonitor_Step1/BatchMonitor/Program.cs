using BatchMonitor.Configuration;
using BatchMonitor.Hubs;
using BatchMonitor.Services;
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
        builder.Services.AddSingleton<IBatchService, MockBatchService>();

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
        app.MapFallbackToPage("/_Host");

        app.Run();
    }
}
