using NxtUI.Core.Models;
using NxtUI.Filtering;
using FluentAssertions;

namespace NxtUI.Tests.Filtering;

/// <summary>
/// Parse-then-evaluate tests against real domain model types.
/// Exercises the full pipeline (parser → AST → evaluator → reflection) and catches
/// field-name mismatches, type-conversion gaps, and semantic regressions early.
/// </summary>
public class FilterIntegrationTests
{
    // ── ServiceStatus ──────────────────────────────────────────────────────

    private static readonly FilterParser SvcParser = new(["ServiceName", "HostName"]);

    private static bool EvalSvc(string filter, ServiceStatus svc) =>
        FilterEvaluator.Evaluate(SvcParser.Parse(filter), svc);

    private static ServiceStatus MakeSvc(
        string name, string host, bool online = true,
        int pid = 1000, int minsAgo = 0) => new()
    {
        ServiceName     = name,
        HostName        = host,
        IsOnline        = online,
        ProcessId       = pid,
        UpdatedDateTime = DateTime.UtcNow.AddMinutes(-minsAgo),
        CreatedDateTime = DateTime.UtcNow.AddDays(-1),
    };

    [Fact]
    public void ServiceStatus_bare_term_matches_name_substring()
    {
        EvalSvc("Loader", MakeSvc("LoaderService", "srv-01")).Should().BeTrue();
        EvalSvc("Loader", MakeSvc("TransformerService", "srv-01")).Should().BeFalse();
    }

    [Fact]
    public void ServiceStatus_bare_term_matches_host()
    {
        EvalSvc("srv-01", MakeSvc("Loader", "srv-01")).Should().BeTrue();
        EvalSvc("srv-01", MakeSvc("Loader", "srv-02")).Should().BeFalse();
    }

    [Fact]
    public void ServiceStatus_exact_host_filter()
    {
        EvalSvc("HostName:=srv-01", MakeSvc("Loader", "srv-01")).Should().BeTrue();
        EvalSvc("HostName:=srv-02", MakeSvc("Loader", "srv-01")).Should().BeFalse();
    }

    [Fact]
    public void ServiceStatus_wildcard_host_filter()
    {
        EvalSvc("HostName:dev1-*", MakeSvc("Loader", "dev1-srv-01")).Should().BeTrue();
        EvalSvc("HostName:dev1-*", MakeSvc("Loader", "dev2-srv-01")).Should().BeFalse();
    }

    [Fact]
    public void ServiceStatus_AND_name_and_host()
    {
        var target     = MakeSvc("Loader", "srv-01");
        var wrongHost  = MakeSvc("Loader", "srv-02");
        var wrongName  = MakeSvc("Exporter", "srv-01");
        EvalSvc("ServiceName:Loader HostName:srv-01", target).Should().BeTrue();
        EvalSvc("ServiceName:Loader HostName:srv-01", wrongHost).Should().BeFalse();
        EvalSvc("ServiceName:Loader HostName:srv-01", wrongName).Should().BeFalse();
    }

    [Fact]
    public void ServiceStatus_OR_across_names()
    {
        EvalSvc("ServiceName:Loader, ServiceName:Transformer", MakeSvc("Loader", "srv-01")).Should().BeTrue();
        EvalSvc("ServiceName:Loader, ServiceName:Transformer", MakeSvc("Transformer", "srv-01")).Should().BeTrue();
        EvalSvc("ServiceName:Loader, ServiceName:Transformer", MakeSvc("Exporter", "srv-01")).Should().BeFalse();
    }

    [Fact]
    public void ServiceStatus_stale_filter_by_UpdatedDateTime()
    {
        var stale  = MakeSvc("Loader", "srv-01", minsAgo: 90);  // 90 min old
        var fresh  = MakeSvc("Loader", "srv-01", minsAgo: 2);   // 2 min old
        var parser = new FilterParser([]);

        // "updated more than 60 minutes ago" — stale only
        FilterEvaluator.Evaluate(parser.Parse("UpdatedDateTime:<-60m"), stale).Should().BeTrue();
        FilterEvaluator.Evaluate(parser.Parse("UpdatedDateTime:<-60m"), fresh).Should().BeFalse();
    }

    [Fact]
    public void ServiceStatus_NOT_excludes_matching_host()
    {
        EvalSvc("!HostName:srv-01", MakeSvc("Loader", "srv-02")).Should().BeTrue();
        EvalSvc("!HostName:srv-01", MakeSvc("Loader", "srv-01")).Should().BeFalse();
    }

    // ── KafkaTopicSummary ──────────────────────────────────────────────────

    private static readonly FilterParser TopicParser = new(["Name"]);

    private static bool EvalTopic(string filter, KafkaTopicSummary t) =>
        FilterEvaluator.Evaluate(TopicParser.Parse(filter), t);

    private static KafkaTopicSummary MakeTopic(
        string name, int parts = 3, long retMs = 86_400_000,
        string policy = "delete", long msgs = 1000) => new()
    {
        Name             = name,
        PartitionCount   = parts,
        RetentionMs      = retMs,
        CleanupPolicy    = policy,
        MessageCount     = msgs,
        ConfigLoaded     = true,
    };

    [Fact]
    public void KafkaTopic_bare_term_matches_name()
    {
        EvalTopic("orders", MakeTopic("orders.events")).Should().BeTrue();
        EvalTopic("orders", MakeTopic("payments.events")).Should().BeFalse();
    }

    [Fact]
    public void KafkaTopic_filter_high_partition_count()
    {
        var parser = new FilterParser([]);
        var busy   = MakeTopic("test", parts: 24);
        var small  = MakeTopic("test", parts: 3);
        FilterEvaluator.Evaluate(parser.Parse("PartitionCount:>12"), busy).Should().BeTrue();
        FilterEvaluator.Evaluate(parser.Parse("PartitionCount:>12"), small).Should().BeFalse();
    }

    [Fact]
    public void KafkaTopic_filter_retention_in_range()
    {
        var parser    = new FilterParser([]);
        var oneDay    = MakeTopic("t1", retMs: 86_400_000);        // 1 day
        var oneWeek   = MakeTopic("t2", retMs: 604_800_000);       // 7 days
        var twoWeeks  = MakeTopic("t3", retMs: 1_209_600_000);     // 14 days

        // 2 days..10 days window
        var ast = parser.Parse("RetentionMs:172800000..864000000");
        FilterEvaluator.Evaluate(ast, oneDay).Should().BeFalse();
        FilterEvaluator.Evaluate(ast, oneWeek).Should().BeTrue();
        FilterEvaluator.Evaluate(ast, twoWeeks).Should().BeFalse();
    }

    [Fact]
    public void KafkaTopic_filter_by_cleanup_policy()
    {
        var compact = MakeTopic("t", policy: "compact");
        var delete  = MakeTopic("t", policy: "delete");
        EvalTopic("CleanupPolicy:compact", compact).Should().BeTrue();
        EvalTopic("CleanupPolicy:compact", delete).Should().BeFalse();
        EvalTopic("CleanupPolicy:delete",  delete).Should().BeTrue();
    }

    [Fact]
    public void KafkaTopic_NOT_compact_excludes_compact_topics()
    {
        var compact = MakeTopic("t", policy: "compact");
        var delete  = MakeTopic("t", policy: "delete");
        EvalTopic("!CleanupPolicy:compact", compact).Should().BeFalse();
        EvalTopic("!CleanupPolicy:compact", delete).Should().BeTrue();
    }

    [Fact]
    public void KafkaTopic_infinite_retention_is_negative_one()
    {
        var parser    = new FilterParser([]);
        var infinite  = MakeTopic("t", retMs: -1);
        FilterEvaluator.Evaluate(parser.Parse("RetentionMs:<0"), infinite).Should().BeTrue();
        FilterEvaluator.Evaluate(parser.Parse("RetentionMs:>0"), infinite).Should().BeFalse();
    }

    [Fact]
    public void KafkaTopic_wildcard_name_matches_topic_family()
    {
        EvalTopic("Name:orders.*", MakeTopic("orders.events")).Should().BeTrue();
        EvalTopic("Name:orders.*", MakeTopic("orders.dead-letter")).Should().BeTrue();
        EvalTopic("Name:orders.*", MakeTopic("payments.events")).Should().BeFalse();
    }

    // ── MongoCollectionSummary ─────────────────────────────────────────────

    private static readonly FilterParser ColParser = new(["Name"]);

    private static bool EvalCol(string filter, MongoCollectionSummary c) =>
        FilterEvaluator.Evaluate(ColParser.Parse(filter), c);

    private static MongoCollectionSummary MakeCol(
        string name, long docs = 0, long storageBytes = 0, bool loaded = true) => new()
    {
        Name             = name,
        DocumentCount    = docs,
        StorageSizeBytes = storageBytes,
        StatsLoaded      = loaded,
    };

    [Fact]
    public void MongoCollection_bare_term_matches_name()
    {
        EvalCol("audit", MakeCol("audit_log")).Should().BeTrue();
        EvalCol("audit", MakeCol("run_events")).Should().BeFalse();
    }

    [Fact]
    public void MongoCollection_filter_large_by_document_count()
    {
        var parser = new FilterParser([]);
        var large  = MakeCol("big",   docs: 1_000_000);
        var small  = MakeCol("small", docs: 100);
        FilterEvaluator.Evaluate(parser.Parse("DocumentCount:>500000"), large).Should().BeTrue();
        FilterEvaluator.Evaluate(parser.Parse("DocumentCount:>500000"), small).Should().BeFalse();
    }

    [Fact]
    public void MongoCollection_Between_doc_count()
    {
        var parser = new FilterParser([]);
        var target = MakeCol("mid", docs: 50_000);
        FilterEvaluator.Evaluate(parser.Parse("DocumentCount:10000..100000"), target).Should().BeTrue();
        FilterEvaluator.Evaluate(parser.Parse("DocumentCount:100001..999999"), target).Should().BeFalse();
    }

    [Fact]
    public void MongoCollection_unloaded_stats_doc_count_is_zero()
    {
        var parser   = new FilterParser([]);
        var unloaded = MakeCol("test", loaded: false);
        FilterEvaluator.Evaluate(parser.Parse("DocumentCount:>0"), unloaded).Should().BeFalse();
        FilterEvaluator.Evaluate(parser.Parse("DocumentCount:<1"), unloaded).Should().BeTrue();
    }

    [Fact]
    public void MongoCollection_wildcard_name_matches_prefix()
    {
        EvalCol("Name:run_*", MakeCol("run_events")).Should().BeTrue();
        EvalCol("Name:run_*", MakeCol("run_steps")).Should().BeTrue();
        EvalCol("Name:run_*", MakeCol("audit_log")).Should().BeFalse();
    }

    [Fact]
    public void MongoCollection_filter_combines_name_and_doc_count()
    {
        var parser = new FilterParser(["Name"]);
        var large  = MakeCol("audit_log", docs: 5_000_000);
        var small  = MakeCol("audit_log", docs: 100);
        var other  = MakeCol("run_events", docs: 5_000_000);

        FilterEvaluator.Evaluate(parser.Parse("Name:audit DocumentCount:>1000000"), large).Should().BeTrue();
        FilterEvaluator.Evaluate(parser.Parse("Name:audit DocumentCount:>1000000"), small).Should().BeFalse();
        FilterEvaluator.Evaluate(parser.Parse("Name:audit DocumentCount:>1000000"), other).Should().BeFalse();
    }
}
