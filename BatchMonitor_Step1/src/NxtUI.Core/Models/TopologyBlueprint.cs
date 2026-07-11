namespace NxtUI.Core.Models;

/// <summary>
/// A resolved <see cref="TopologyVariant"/> compiled for merging into a computed
/// <see cref="Topology"/> — the declared services (for skeleton nodes + node decoration),
/// the declared service→service edges, and the layout. Produced by
/// <see cref="Compile"/>; consumed by <c>TopologyComputationService</c>.
/// </summary>
public sealed class TopologyBlueprint
{
    public LayoutHint? Layout { get; init; }

    /// <summary>Declared services in declaration order (order matters: first glob match wins).</summary>
    public IReadOnlyList<ServiceHint> Services { get; init; } = [];

    /// <summary>Declared edges as (fromService, toService) name pairs — names may be literal or glob.</summary>
    public IReadOnlyList<(string From, string To)> DeclaredEdges { get; init; } = [];

    /// <summary>
    /// Picks the variant to use: the first whose <see cref="VariantMatch.AnyService"/> glob
    /// matches a service seen so far, else <see cref="TopologyHintFile.Default"/>. Returns null
    /// when nothing matches and there's no default (⇒ no blueprint, pure runtime inference).
    /// </summary>
    public static TopologyVariant? SelectVariant(TopologyHintFile file, IEnumerable<string> seenServices)
    {
        var seen = seenServices as ICollection<string> ?? seenServices.ToList();
        foreach (var variant in file.Variants)
        {
            var pattern = variant.Match?.AnyService;
            if (!string.IsNullOrWhiteSpace(pattern) && seen.Any(s => Glob.IsMatch(s, pattern)))
                return variant;
        }
        return file.Default;
    }

    /// <summary>Compiles a chosen variant into a blueprint, deriving edges from shared publish/subscribe Targets plus any explicit edges.</summary>
    public static TopologyBlueprint Compile(TopologyVariant variant)
    {
        var edges = new List<(string From, string To)>();

        // Derived edges: a producer's publish token == a consumer's subscribe token (exact,
        // case-insensitive). Globs form a declared edge only when the tokens are string-equal;
        // otherwise the edge is left for runtime, where globs resolve against concrete targets.
        foreach (var producer in variant.Services)
        foreach (var target in producer.Publishes)
        foreach (var consumer in variant.Services)
        {
            if (ReferenceEquals(producer, consumer)) continue;
            if (consumer.Subscribes.Any(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase)))
                edges.Add((producer.Name, consumer.Name));
        }

        // Explicit edges shorthand.
        foreach (var e in variant.Edges)
            if (!string.IsNullOrWhiteSpace(e.From) && !string.IsNullOrWhiteSpace(e.To))
                edges.Add((e.From, e.To));

        return new TopologyBlueprint
        {
            Layout = variant.Layout,
            Services = variant.Services,
            DeclaredEdges = Dedup(edges),
        };
    }

    private static List<(string From, string To)> Dedup(List<(string From, string To)> edges)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();
        foreach (var e in edges)
            if (seen.Add($"{e.From}{e.To}"))
                result.Add(e);
        return result;
    }
}
