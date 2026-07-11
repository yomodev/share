using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Loads optional per-run-type topology hints from <c>{BasePath}/topology/{runType}.json</c>
/// (see <see cref="EnvironmentConfigOptions.BasePath"/>). Files are parsed once and cached
/// for the process lifetime; a missing/invalid file resolves to null (the graph then falls
/// back to pure runtime inference — hints are advisory). Lookup key is the run's Type,
/// lowercased; the flow shape doesn't vary by environment, so this is not env-scoped.
/// </summary>
public sealed class TopologyHintLoader(EnvironmentConfigOptions options, ILogger<TopologyHintLoader> log)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Cached including the "not found" result (null) so we don't stat the disk on every poll.
    private readonly ConcurrentDictionary<string, TopologyHintFile?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the hint file for a run type, or null if none exists / failed to parse.</summary>
    public TopologyHintFile? Get(string? runType)
    {
        if (string.IsNullOrWhiteSpace(runType)) return null;
        return _cache.GetOrAdd(runType, Load);
    }

    private TopologyHintFile? Load(string runType)
    {
        var path = Path.Combine(options.BasePath, "topology", $"{runType.ToLowerInvariant()}.json");
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TopologyHintFile>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to load topology hint from {Path}", path);
            return null;
        }
    }
}
