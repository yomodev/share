using NxtUI.Core.Models;

namespace NxtUI.Filtering;

/// <summary>
/// Walks a parsed filter AST and extracts Kafka-level seek terms
/// (partition, offset, timestamp, latest) into a <see cref="KafkaSeekDirective"/>.
/// Returns the remaining AST (non-Kafka terms) for client-side payload filtering.
/// </summary>
public static class KafkaFilterExtractor
{
    private static readonly HashSet<string> _kafkaFields =
        new(StringComparer.OrdinalIgnoreCase) { "partition", "offset", "timestamp", "latest" };

    public static (KafkaSeekDirective Directive, FilterNode? Remaining) Extract(FilterNode? root)
    {
        var builder  = new DirectiveBuilder();
        var remaining = Visit(root, builder);
        return (builder.Build(), remaining);
    }

    // ── AST visitor ──────────────────────────────────────────────────────────

    private static FilterNode? Visit(FilterNode? node, DirectiveBuilder b) => node switch
    {
        null            => null,
        AndNode and     => Merge(Visit(and.Left, b), Visit(and.Right, b)),
        OrNode or       => new OrNode(Visit(or.Left,  b) ?? TrueNode.Instance,
                                      Visit(or.Right, b) ?? TrueNode.Instance),
        NotNode not     => new NotNode(Visit(not.Operand, b) ?? TrueNode.Instance),
        FieldTermNode t => IsKafka(t) ? Consume(t, b) : t,
        _               => node,
    };

    private static bool IsKafka(FieldTermNode t) =>
        _kafkaFields.Contains(t.Field);

    private static FilterNode? Consume(FieldTermNode t, DirectiveBuilder b)
    {
        var field = t.Field.ToLowerInvariant();

        switch (field)
        {
            case "latest" when t.Value is NumberValue nv:
                b.Latest = (int)nv.Value;
                break;

            case "partition":
                b.AddPartitions(t);
                break;

            case "offset":
                switch ((t.MatchType, t.Value))
                {
                    case (MatchType.Contains or MatchType.Exact, NumberValue nv):
                        b.OffsetFrom = (long)nv.Value;
                        break;
                    case (MatchType.GreaterThan, NumberValue nv):
                        b.OffsetFrom = (long)nv.Value + 1;
                        break;
                    case (MatchType.GreaterThanOrEqual, NumberValue nv):
                        b.OffsetFrom = (long)nv.Value;
                        break;
                    case (MatchType.LessThan, NumberValue nv):
                        b.OffsetTo = (long)nv.Value - 1;
                        break;
                    case (MatchType.LessThanOrEqual, NumberValue nv):
                        b.OffsetTo = (long)nv.Value;
                        break;
                    case (MatchType.Between, RangeValue { Low: NumberValue lo, High: NumberValue hi }):
                        b.OffsetFrom = (long)lo.Value;
                        b.OffsetTo   = (long)hi.Value;
                        break;
                }
                break;

            case "timestamp":
                switch ((t.MatchType, t.Value))
                {
                    case (MatchType.GreaterThan or MatchType.GreaterThanOrEqual, DateValue dv):
                        b.TimestampFrom = dv.Value;
                        break;
                    case (MatchType.LessThan or MatchType.LessThanOrEqual, DateValue dv):
                        b.TimestampTo = dv.Value;
                        break;
                    case (MatchType.Between, RangeValue { Low: DateValue lo, High: DateValue hi }):
                        b.TimestampFrom = lo.Value;
                        b.TimestampTo   = hi.Value;
                        break;
                }
                break;
        }

        return null; // consumed — remove from remaining AST
    }

    private static FilterNode? Merge(FilterNode? left, FilterNode? right) =>
        (left, right) switch
        {
            (null, null) => null,
            (null, _)    => right,
            (_, null)    => left,
            _            => new AndNode(left, right),
        };

    // ── Builder ───────────────────────────────────────────────────────────────

    private sealed class DirectiveBuilder
    {
        private HashSet<int>? _partitions;
        public long?      OffsetFrom    { get; set; }
        public long?      OffsetTo      { get; set; }
        public DateTime?  TimestampFrom { get; set; }
        public DateTime?  TimestampTo   { get; set; }
        public int?       Latest        { get; set; }

        public void AddPartitions(FieldTermNode t)
        {
            _partitions ??= [];

            switch ((t.MatchType, t.Value))
            {
                case (_, NumberValue nv):
                    _partitions.Add((int)nv.Value);
                    break;

                case (MatchType.Between, RangeValue { Low: NumberValue lo, High: NumberValue hi }):
                    for (var p = (int)lo.Value; p <= (int)hi.Value; p++)
                        _partitions.Add(p);
                    break;

                case (_, StringValue sv):
                    // comma-separated list: "0,2,4"
                    foreach (var part in sv.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(part.Trim(), out var p)) _partitions.Add(p);
                    break;
            }
        }

        public KafkaSeekDirective Build() => new()
        {
            Partitions    = _partitions is { Count: > 0 } ? _partitions : null,
            OffsetFrom    = OffsetFrom,
            OffsetTo      = OffsetTo,
            TimestampFrom = TimestampFrom,
            TimestampTo   = TimestampTo,
            Latest        = Latest,
        };
    }
}

// Sentinel used when an AND branch collapses to nothing
file sealed record TrueNode : FilterNode
{
    public static readonly TrueNode Instance = new();
}
