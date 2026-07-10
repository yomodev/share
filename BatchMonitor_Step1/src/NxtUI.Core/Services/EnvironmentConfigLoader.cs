using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NxtUI.Core.Configuration;

namespace NxtUI.Core.Services;

public class EnvironmentConfigOptions
{
    public string BasePath { get; set; } = "config";
}

/// <summary>
/// Loads per-environment connection settings from <c>config/{envId}.json</c>.
/// Files are parsed once and cached for the lifetime of the process.
/// Two environments that share identical connection details will reuse the same
/// client instance via the connection factories.
/// </summary>
public sealed class EnvironmentConfigLoader(
    EnvironmentConfigOptions options,
    ILogger<EnvironmentConfigLoader> log)
{
    private readonly ConcurrentDictionary<string, EnvironmentConfig> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public KafkaSettings GetKafka(string envId) => Get(envId).Kafka;
    public MongoEnvSettings GetMongo(string envId) => Get(envId).Mongo;
    public SqlSettings GetSql(string envId) => Get(envId).Sql;

    private EnvironmentConfig Get(string envId) =>
        _cache.GetOrAdd(envId, Load);

    private EnvironmentConfig Load(string envId)
    {
        var config = new EnvironmentConfig();
        var path = string.Empty;

        try
        {
            path = Path.Combine(options.BasePath, $"{envId.ToLowerInvariant()}.json");
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<EnvironmentConfig>(json, _jsonOptions) ?? new EnvironmentConfig();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to load environment config from {Path}", path);
        }

        return config;
    }
}

public sealed class EnvironmentConfig
{
    public KafkaSettings Kafka { get; set; } = new();
    public MongoEnvSettings Mongo { get; set; } = new();
    public SqlSettings Sql { get; set; } = new();
}
