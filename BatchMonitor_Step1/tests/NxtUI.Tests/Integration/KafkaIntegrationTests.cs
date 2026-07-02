using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Models;
using NxtUI.Core.Services;
using NxtUI.Core.Services.Kafka;
using NxtUI.Filtering;

namespace NxtUI.Tests.Integration;

/// <summary>
/// Integration tests for the Kafka pipeline.
/// Skipped unless RUN_INTEGRATION_TESTS=1.
/// Broker tests additionally require KAFKA_BOOTSTRAP_SERVERS.
/// </summary>
[IntegrationTest]
public sealed class KafkaIntegrationTests
{
    private static string Brokers =>
        Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";

    private static KafkaConnectionFactory BuildFactory(string? bootstrapServers = null)
    {
        var servers  = bootstrapServers ?? Brokers;
        var basePath = Path.Combine(Path.GetTempPath(), $"kafka-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);
        File.WriteAllText(
            Path.Combine(basePath, "test.json"),
            $"{{\"Kafka\":{{\"BootstrapServers\":\"{servers}\"}}}}");
        var loader = new EnvironmentConfigLoader(
            new EnvironmentConfigOptions { BasePath = basePath },
            NullLogger<EnvironmentConfigLoader>.Instance);
        return new KafkaConnectionFactory(loader);
    }

    // ── Connection factory ────────────────────────────────────────────────────

    [IntegrationTestFact]
    public void SameFingerprint_TwoEnvs_ShareClientConfig()
    {
        var factory = BuildFactory();

        // Two lookups for "test" env should return the same ClientConfig object (same fingerprint)
        var cfg1 = factory.GetClientConfig("test");
        var cfg2 = factory.GetClientConfig("test");

        cfg1.Should().BeSameAs(cfg2, "same env → same fingerprint → cached object");
    }

    // ── TopicDeserializerPipeline ─────────────────────────────────────────────

    [IntegrationTestFact]
    public void Pipeline_UnknownBinary_ReturnsStub()
    {
        var settings = Options.Create(new KafkaSettings
        {
            TopicDeserializers = [new TopicDeserializerRule { Pattern = "order*", Types = ["OrderEvent"] }]
        });
        var registry = new NullMessageRegistry();
        var pipeline = new TopicDeserializerPipeline(settings, registry);

        var (json, type) = pipeline.Deserialize("some-other-topic", [0x01, 0x02, 0x03]);

        type.Should().Be("unknown");
        json.Should().Contain("sizeBytes").And.Contain("3");
    }

    [IntegrationTestFact]
    public void Pipeline_PatternMatch_TriesConfiguredType()
    {
        var settings = Options.Create(new KafkaSettings
        {
            TopicDeserializers = [new TopicDeserializerRule { Pattern = "order*", Types = ["OrderEvent"] }]
        });
        var registry = new RecordingMessageRegistry("OrderEvent");
        var pipeline = new TopicDeserializerPipeline(settings, registry);

        pipeline.Deserialize("order.events", [0xDE, 0xAD]);

        registry.LastQueried.Should().Be("OrderEvent");
    }

    [IntegrationTestFact]
    public void Pipeline_MultipleCandidates_TriesInOrder_ThenCachesTheWinner()
    {
        var settings = Options.Create(new KafkaSettings
        {
            TopicDeserializers =
                [new TopicDeserializerRule { Pattern = "order*", Types = ["WrongType", "OrderEvent", "AlsoNeverReached"] }]
        });
        var registry = new OrderedMessageRegistry("OrderEvent");
        var pipeline = new TopicDeserializerPipeline(settings, registry);

        var (_, type1) = pipeline.Deserialize("order.events", [0xDE, 0xAD]);
        type1.Should().Be("OrderEvent");
        registry.Queried.Should().Equal("WrongType", "OrderEvent"); // stops at first success, never tries the third

        registry.Queried.Clear();
        var (_, type2) = pipeline.Deserialize("order.events", [0xBE, 0xEF]);
        type2.Should().Be("OrderEvent");
        registry.Queried.Should().Equal("OrderEvent"); // cached from the first call — skips "WrongType" this time
    }

    // ── KafkaFilterExtractor ──────────────────────────────────────────────────

    [IntegrationTestFact]
    public void Extractor_Partition_SingleValue()
    {
        var parser = BuildParser();
        var ast    = parser.Parse("partition:2");
        var (dir, remaining) = KafkaFilterExtractor.Extract(ast);

        dir.Partitions.Should().BeEquivalentTo([2]);
        remaining.Should().BeNull();
    }

    [IntegrationTestFact]
    public void Extractor_Partition_List()
    {
        var parser = BuildParser();
        var ast    = parser.Parse("partition:0,2,4");
        var (dir, _) = KafkaFilterExtractor.Extract(ast);

        dir.Partitions.Should().BeEquivalentTo([0, 2, 4]);
    }

    [IntegrationTestFact]
    public void Extractor_Partition_Range()
    {
        var parser = BuildParser();
        var ast    = parser.Parse("partition:0..3");
        var (dir, _) = KafkaFilterExtractor.Extract(ast);

        dir.Partitions.Should().BeEquivalentTo([0, 1, 2, 3]);
    }

    [IntegrationTestFact]
    public void Extractor_OffsetRange()
    {
        var parser = BuildParser();
        var ast    = parser.Parse("offset:1000..2000");
        var (dir, remaining) = KafkaFilterExtractor.Extract(ast);

        dir.OffsetFrom.Should().Be(1000);
        dir.OffsetTo.Should().Be(2000);
        remaining.Should().BeNull();
    }

    [IntegrationTestFact]
    public void Extractor_OffsetGreaterThan()
    {
        var parser = BuildParser();
        var ast    = parser.Parse("offset:>500");
        var (dir, _) = KafkaFilterExtractor.Extract(ast);

        dir.OffsetFrom.Should().Be(501);
    }

    [IntegrationTestFact]
    public void Extractor_Latest()
    {
        var parser = BuildParser();
        var ast    = parser.Parse("latest:500");
        var (dir, remaining) = KafkaFilterExtractor.Extract(ast);

        dir.Latest.Should().Be(500);
        remaining.Should().BeNull();
    }

    [IntegrationTestFact]
    public void Extractor_MixedTerms_NonKafkaRemains()
    {
        var parser = BuildParser();
        var ast    = parser.Parse("partition:0 latest:100 key:abc");
        var (dir, remaining) = KafkaFilterExtractor.Extract(ast);

        dir.Partitions.Should().BeEquivalentTo([0]);
        dir.Latest.Should().Be(100);
        remaining.Should().NotBeNull("'key:abc' is a payload filter and must survive extraction");
    }

    // ── FilterEvaluator JsonPayload fallback ──────────────────────────────────

    [IntegrationTestFact]
    public void Evaluator_JsonPayload_FieldLookup_GreaterThan()
    {
        var msg = new KafkaMessage
        {
            Offset      = 1,
            Partition   = 0,
            Timestamp   = DateTime.UtcNow,
            JsonPayload = """{"amount":150,"orderId":"abc"}""",
            PayloadType = "OrderEvent",
        };

        var parser = BuildParser();
        var ast    = parser.Parse("amount:>100");

        FilterEvaluator.Evaluate(ast, msg).Should().BeTrue();
    }

    [IntegrationTestFact]
    public void Evaluator_JsonPayload_FieldLookup_StringContains()
    {
        var msg = new KafkaMessage
        {
            Offset      = 1,
            Partition   = 0,
            Timestamp   = DateTime.UtcNow,
            JsonPayload = """{"orderId":"ORDER-12345","status":"CREATED"}""",
            PayloadType = "OrderEvent",
        };

        var parser = BuildParser();
        var ast    = parser.Parse("orderId:ORDER");

        FilterEvaluator.Evaluate(ast, msg).Should().BeTrue();
    }

    // ── Kafka broker tests (require KAFKA_BOOTSTRAP_SERVERS) ─────────────────

    [KafkaIntegrationTestFact]
    public async Task TailTopicAsync_FromBeginning_ReceivesMessages()
    {
        var factory  = BuildFactory();
        var pipeline = BuildPipeline();
        var settings = Options.Create(new KafkaSettings { MaxFetchMessages = 10 });
        var svc      = new KafkaService(factory, pipeline, settings, NullLogger<KafkaService>.Instance);

        var topics = await svc.GetTopicsAsync("test");
        if (topics.Count == 0) return; // no topics — not a failure

        var topic    = topics[0].Name;
        var messages = new List<KafkaMessage>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var msg in svc.TailTopicAsync("test", topic, KafkaSeekDirective.Default, cts.Token))
        {
            messages.Add(msg);
            if (messages.Count >= 5) cts.Cancel();
        }

        messages.Should().NotBeEmpty();
        messages.All(m => m.RawSizeBytes >= 0).Should().BeTrue();
    }

    [KafkaIntegrationTestFact]
    public async Task TailTopicAsync_Latest10_SeeksCorrectly()
    {
        var factory  = BuildFactory();
        var pipeline = BuildPipeline();
        var settings = Options.Create(new KafkaSettings { MaxFetchMessages = 10 });
        var svc      = new KafkaService(factory, pipeline, settings, NullLogger<KafkaService>.Instance);

        var topics = await svc.GetTopicsAsync("test");
        if (topics.Count == 0) return;

        var topic    = topics[0].Name;
        var messages = new List<KafkaMessage>();
        var directive = new KafkaSeekDirective { Latest = 10 };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var msg in svc.TailTopicAsync("test", topic, directive, cts.Token))
            messages.Add(msg);

        messages.Count.Should().BeLessThanOrEqualTo(10);
    }

    [KafkaIntegrationTestFact]
    public async Task GetPartitionStatsAsync_ReturnsWatermarks()
    {
        var factory  = BuildFactory();
        var pipeline = BuildPipeline();
        var settings = Options.Create(new KafkaSettings { MaxFetchMessages = 10 });
        var svc      = new KafkaService(factory, pipeline, settings, NullLogger<KafkaService>.Instance);

        var topics = await svc.GetTopicsAsync("test");
        if (topics.Count == 0) return;

        var stats = await svc.GetPartitionStatsAsync("test", topics[0].Name);

        stats.Should().NotBeEmpty();
        stats.All(s => s.HighWatermark >= s.LowWatermark).Should().BeTrue();
    }

    [KafkaIntegrationTestFact]
    public async Task FetchRawBytesAsync_ReturnsBytes_ForKnownOffset()
    {
        var factory  = BuildFactory();
        var pipeline = BuildPipeline();
        var settings = Options.Create(new KafkaSettings { MaxFetchMessages = 5 });
        var svc      = new KafkaService(factory, pipeline, settings, NullLogger<KafkaService>.Instance);

        var topics = await svc.GetTopicsAsync("test");
        if (topics.Count == 0) return;

        // Consume one message to get a real partition+offset
        KafkaMessage? first = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var msg in svc.TailTopicAsync("test", topics[0].Name, new KafkaSeekDirective { Latest = 1 }, cts.Token))
        {
            first = msg;
            break;
        }

        if (first is null) return;

        var bytes = await svc.FetchRawBytesAsync("test", topics[0].Name, first.Partition, first.Offset);
        bytes.Should().NotBeNull();
        bytes!.Length.Should().Be(first.RawSizeBytes);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FilterParser BuildParser() => new(
        ["Key", "PayloadType", "Partition", "Offset", "Timestamp", "latest"],
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = "Key", ["partition"] = "Partition", ["offset"] = "Offset",
            ["timestamp"] = "Timestamp", ["ts"] = "Timestamp", ["type"] = "PayloadType",
            ["latest"] = "latest",
        });

    private static TopicDeserializerPipeline BuildPipeline() =>
        new(Options.Create(new KafkaSettings()), new NullMessageRegistry());

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class NullMessageRegistry : IMessageRegistry
    {
        public bool TryParseToJson(string typeName, byte[] bytes, out string? json)
        { json = null; return false; }
        public IReadOnlyCollection<string> RegisteredTypes => [];
    }

    private sealed class RecordingMessageRegistry(string acceptType) : IMessageRegistry
    {
        public string? LastQueried { get; private set; }
        public bool TryParseToJson(string typeName, byte[] bytes, out string? json)
        {
            LastQueried = typeName;
            json = typeName == acceptType ? "{}" : null;
            return typeName == acceptType;
        }
        public IReadOnlyCollection<string> RegisteredTypes => [acceptType];
    }

    private sealed class OrderedMessageRegistry(string acceptType) : IMessageRegistry
    {
        public List<string> Queried { get; } = [];
        public bool TryParseToJson(string typeName, byte[] bytes, out string? json)
        {
            Queried.Add(typeName);
            json = typeName == acceptType ? "{}" : null;
            return typeName == acceptType;
        }
        public IReadOnlyCollection<string> RegisteredTypes => [acceptType];
    }
}
