using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Services;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Verifies that IEnvironmentService correctly reflects the server lists
/// configured in appsettings.json, so mismatches between config and service
/// are caught before they reach the UI.
/// </summary>
public sealed class EnvironmentConfigTests(ServiceFixture fix, ITestOutputHelper out_)
    : IClassFixture<ServiceFixture>
{
    [Fact]
    public void GetAll_Returns_All_Configured_Environments()
    {
        var envSvc   = fix.Services.GetRequiredService<IEnvironmentService>();
        var settings = fix.Services.GetRequiredService<IOptions<AppSettings>>().Value;

        var all = envSvc.GetAll();

        out_.WriteLine($"Configured: {settings.Environments.Count} environments");
        out_.WriteLine($"Returned:   {all.Count} environments");

        Assert.Equal(settings.Environments.Count, all.Count);

        foreach (var def in settings.Environments)
        {
            var found = all.FirstOrDefault(e => e.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase));
            out_.WriteLine($"  {def.Id} ({def.Tier}): {(found is not null ? "found" : "MISSING")}");
            Assert.NotNull(found);
        }
    }

    [Fact]
    public void GetServers_Returns_Correct_Count_For_Each_Environment()
    {
        var envSvc   = fix.Services.GetRequiredService<IEnvironmentService>();
        var settings = fix.Services.GetRequiredService<IOptions<AppSettings>>().Value;

        foreach (var def in settings.Environments)
        {
            var servers = envSvc.GetServers(def.Id);
            out_.WriteLine($"  {def.Id}: {servers.Count} servers (expected {def.Servers.Count})");
            Assert.Equal(def.Servers.Count, servers.Count);
        }
    }

    [Fact]
    public void GetServers_Returns_Correct_Hostnames_For_Each_Environment()
    {
        var envSvc   = fix.Services.GetRequiredService<IEnvironmentService>();
        var settings = fix.Services.GetRequiredService<IOptions<AppSettings>>().Value;

        foreach (var def in settings.Environments)
        {
            var actual = envSvc.GetServers(def.Id);
            foreach (var expected in def.Servers)
            {
                out_.WriteLine($"  {def.Id}: checking for '{expected}'");
                Assert.Contains(expected, actual, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void GetServers_Returns_Empty_For_Unknown_Environment()
    {
        var envSvc = fix.Services.GetRequiredService<IEnvironmentService>();
        var result = envSvc.GetServers("__nonexistent_env__");
        Assert.Empty(result);
    }

    [Fact]
    public void GetById_Returns_Correct_Env_Or_Null()
    {
        var envSvc   = fix.Services.GetRequiredService<IEnvironmentService>();
        var settings = fix.Services.GetRequiredService<IOptions<AppSettings>>().Value;

        foreach (var def in settings.Environments)
        {
            var env = envSvc.GetById(def.Id);
            Assert.NotNull(env);
            Assert.Equal(def.Id, env.Id, StringComparer.OrdinalIgnoreCase);
            out_.WriteLine($"  {def.Id}: label='{env.Label}', tier='{def.Tier}'");
        }

        Assert.Null(envSvc.GetById("__nonexistent__"));
    }
}
