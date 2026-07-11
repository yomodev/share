using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NxtUI.Core.Configuration;
using NxtUI.Core.Services;
using NxtUI.Core.Services.Mongo;

namespace NxtUI.Tests.Integration;

/// <summary>
/// Integration tests for MongoConnectionFactory against a real MongoDB instance.
/// Skipped unless RUN_INTEGRATION_TESTS=1. Defaults to mongodb://localhost:27017 (no auth);
/// override with MONGO_CONNECTION_STRING for a different instance. These exercise the actual
/// connection-string resolution (global template + per-env {server}/{database}), fingerprint
/// caching, and timeout behavior — none of which the Performance/*Tests.cs suite touches,
/// since those run against MockMongoService and never construct a real MongoClient.
/// </summary>
[IntegrationTest]
public sealed class MongoConnectionFactoryIntegrationTests
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING") ?? "mongodb://localhost:27017";

    // Builds a factory with a fresh temp config/ folder so each test's env(s) are isolated.
    // envServerNames: envId -> ServerName written to that env's config/{env}.json (or null to
    // omit ServerName entirely, e.g. to test the "template needs {server} but none configured" path).
    private static MongoConnectionFactory BuildFactory(
        string? connectionTemplate = null,
        int? timeoutMs = null,
        params (string EnvId, string? ServerName, string? DatabaseName)[] envs)
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"mongo-conn-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);
        foreach (var (envId, serverName, databaseName) in envs)
        {
            var body = string.Join(",", new[]
            {
                serverName is not null ? $"\"ServerName\":\"{serverName}\"" : null,
                databaseName is not null ? $"\"DatabaseName\":\"{databaseName}\"" : null,
            }.Where(s => s is not null));
            File.WriteAllText(Path.Combine(basePath, $"{envId.ToLowerInvariant()}.json"), $"{{\"Mongo\":{{{body}}}}}");
        }

        var loader = new EnvironmentConfigLoader(
            new EnvironmentConfigOptions { BasePath = basePath }, NullLogger<EnvironmentConfigLoader>.Instance);

        var settings = Options.Create(new MongoSettings
        {
            ConnectionString = connectionTemplate ?? ConnectionString,
            DatabasePrefix = "IntegrationTestDb",
            ConnectionTimeoutMs = timeoutMs ?? 5000,
            ServerSelectionTimeoutMs = timeoutMs ?? 5000,
        });

        return new MongoConnectionFactory(loader, settings, NullLogger<MongoConnectionFactory>.Instance);
    }

    [IntegrationTestFact]
    public async Task GetClientAsync_ConnectsAndPings()
    {
        var factory = BuildFactory(envs: ("test", null, null));
        var ct = TestContext.Current.CancellationToken;

        var client = await factory.GetClientAsync("test", ct);
        var result = await client.GetDatabase("admin")
            .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);

        result.Should().NotBeNull();
        result["ok"].ToDouble().Should().Be(1.0);
    }

    [IntegrationTestFact]
    public void IsConfigured_True_When_Template_Has_No_Server_Placeholder()
    {
        var factory = BuildFactory(envs: ("test", null, null));
        factory.IsConfigured("test").Should().BeTrue();
    }

    [IntegrationTestFact]
    public void IsConfigured_False_When_Template_Needs_Server_But_None_Configured()
    {
        var factory = BuildFactory(connectionTemplate: "mongodb://{server}:27017", envs: ("noserver", null, null));
        factory.IsConfigured("noserver").Should().BeFalse();
    }

    [IntegrationTestFact]
    public void IsConfigured_True_When_Server_Placeholder_Resolved_From_EnvConfig()
    {
        var factory = BuildFactory(connectionTemplate: "mongodb://{server}:27017", envs: ("withserver", "localhost", null));
        factory.IsConfigured("withserver").Should().BeTrue();
    }

    [IntegrationTestFact]
    public async Task GetClientAsync_SameEnv_ReturnsSameCachedClient()
    {
        var factory = BuildFactory(envs: ("test", null, null));
        var ct = TestContext.Current.CancellationToken;

        var client1 = await factory.GetClientAsync("test", ct);
        var client2 = await factory.GetClientAsync("test", ct);

        client1.Should().BeSameAs(client2, "same env → same resolved connection string → cached client");
    }

    [IntegrationTestFact]
    public async Task GetClientAsync_TwoEnvsResolvingToSameConnectionString_ShareOneClient()
    {
        // Both envs' {server} resolves to "localhost" via the template, so they share a
        // fingerprint even though the per-env config differs (proves fingerprinting is on the
        // RESOLVED string, not the raw template or env id).
        var factory = BuildFactory(
            connectionTemplate: "mongodb://{server}:27017",
            envs: [("env-a", "localhost", null), ("env-b", "localhost", null)]);
        var ct = TestContext.Current.CancellationToken;

        var clientA = await factory.GetClientAsync("env-a", ct);
        var clientB = await factory.GetClientAsync("env-b", ct);

        clientA.Should().BeSameAs(clientB);
    }

    [IntegrationTestFact]
    public void GetDatabase_UsesPerEnvDatabaseName_OrFallsBackToPrefixPlusEnvId()
    {
        var factory = BuildFactory(envs: [("with-db", null, "CustomDb"), ("without-db", null, null)]);

        factory.GetDatabase("with-db").DatabaseNamespace.DatabaseName.Should().Be("CustomDb");
        factory.GetDatabase("without-db").DatabaseNamespace.DatabaseName.Should().Be("IntegrationTestDb_without-db");
    }

    [IntegrationTestFact]
    public async Task GetClientAsync_UnreachableHost_FailsWithinConfiguredTimeout_NotHanging()
    {
        // 10.255.255.1 is a non-routable address commonly used to force a connect timeout
        // rather than an immediate "connection refused".
        var factory = BuildFactory(
            connectionTemplate: "mongodb://10.255.255.1:27017",
            timeoutMs: 3000,
            envs: ("unreachable", null, null));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Func<Task> act = async () =>
        {
            var client = await factory.GetClientAsync("unreachable", TestContext.Current.CancellationToken);
            await client.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: TestContext.Current.CancellationToken);
        };

        await act.Should().ThrowAsync<Exception>();
        // Generous upper bound — proves it fails on the CONFIGURED timeout (~3s), not the
        // driver's much longer default.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [IntegrationTestFact]
    public void MissingConnectionStringTemplate_IsNotConfigured()
    {
        var factory = BuildFactory(connectionTemplate: "", envs: ("blank", null, null));
        factory.IsConfigured("blank").Should().BeFalse();
    }
}
