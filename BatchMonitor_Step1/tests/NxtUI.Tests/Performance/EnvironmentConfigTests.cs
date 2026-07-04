using AwesomeAssertions;
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

        all.Count.Should().Be(settings.Environments.Count);

        foreach (var def in settings.Environments)
        {
            var found = all.FirstOrDefault(e => e.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase));
            out_.WriteLine($"  {def.Id} ({def.Tier}): {(found is not null ? "found" : "MISSING")}");
            found.Should().NotBeNull();
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
            servers.Count.Should().Be(def.Servers.Count);
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
                actual.Should().Contain(s => s.Equals(expected, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void GetServers_Returns_Empty_For_Unknown_Environment()
    {
        var envSvc = fix.Services.GetRequiredService<IEnvironmentService>();
        var result = envSvc.GetServers("__nonexistent_env__");
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetById_Returns_Correct_Env_Or_Null()
    {
        var envSvc   = fix.Services.GetRequiredService<IEnvironmentService>();
        var settings = fix.Services.GetRequiredService<IOptions<AppSettings>>().Value;

        foreach (var def in settings.Environments)
        {
            var env = envSvc.GetById(def.Id);
            env.Should().NotBeNull();
            env!.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
            out_.WriteLine($"  {def.Id}: label='{env.Label}', tier='{def.Tier}'");
        }

        envSvc.GetById("__nonexistent__").Should().BeNull();
    }
}
