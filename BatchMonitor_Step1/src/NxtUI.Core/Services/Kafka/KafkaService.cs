using System.Threading.Channels;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NxtUI.Core.Configuration;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services.Kafka;

public class KafkaService(
    KafkaConnectionFactory factory,
    TopicDeserializerPipeline pipeline,
    IOptions<KafkaSettings> settings,
    ILogger<KafkaService> log) : IKafkaService
{
    private readonly KafkaSettings _global = settings.Value;

    // ── IKafkaMonitor ────────────────────────────────────────────────────────

    public Task<KafkaClusterInfo> GetClusterInfoAsync(string env, CancellationToken ct = default)
    {
        log.LogDebug("kafka [{Env}]: cluster info", env);
        // admin.GetMetadata is a synchronous blocking librdkafka call (no async overload).
        // Run it on a thread-pool thread so it never blocks a Blazor circuit's UI thread.
        return Task.Run(() =>
        {
            using var admin = BuildAdmin(env);
            var meta = admin.GetMetadata(TimeSpan.FromSeconds(10));
            return new KafkaClusterInfo
            {
                ClusterId = meta.OriginatingBrokerId.ToString(),
                ControllerId = meta.OriginatingBrokerId,
                Brokers = [.. meta.Brokers.Select(b => new KafkaBroker
                    { Id = b.BrokerId, Host = b.Host, Port = b.Port, IsOnline = true })],
            };
        }, ct);
    }

    public Task<IReadOnlyList<KafkaTopicSummary>> GetTopicsAsync(string env, CancellationToken ct = default)
    {
        log.LogDebug("kafka [{Env}]: listing topics", env);
        // admin.GetMetadata blocks synchronously — offload so the UI thread stays free.
        return Task.Run<IReadOnlyList<KafkaTopicSummary>>(() =>
        {
            using var admin = BuildAdmin(env);
            var meta = admin.GetMetadata(TimeSpan.FromSeconds(10));

            return [.. meta.Topics
                .Where(t => !t.Error.IsError && !t.Topic.StartsWith("__"))
                .Select(t => new KafkaTopicSummary
                {
                    Name              = t.Topic,
                    PartitionCount    = t.Partitions.Count,
                    ReplicationFactor = t.Partitions.FirstOrDefault()?.Replicas.Length ?? 1,
                    CleanupPolicy     = "delete",
                    RetentionMs       = 604_800_000,
                })
                .OrderBy(t => t.Name)];
        }, ct);
    }

    public async Task<KafkaTopicConfig> GetTopicConfigAsync(string env, string topicName, CancellationToken ct = default)
    {
        log.LogDebug("kafka [{Env}]: config for '{Topic}'", env, topicName);
        using var admin = BuildAdmin(env);

        var results = await admin.DescribeConfigsAsync(
            [new ConfigResource { Type = ResourceType.Topic, Name = topicName }]);

        var cfg = results.First();
        long Lng(string k, long d) =>
            cfg.Entries.TryGetValue(k, out var e) && long.TryParse(e.Value, out var v) ? v : d;
        int Int(string k, int d) =>
            cfg.Entries.TryGetValue(k, out var e) && int.TryParse(e.Value, out var v) ? v : d;
        string Str(string k, string d) =>
            cfg.Entries.TryGetValue(k, out var e) ? e.Value : d;

        // GetMetadata blocks synchronously — offload it (DescribeConfigsAsync above is already async).
        var meta = await Task.Run(() => admin.GetMetadata(topicName, TimeSpan.FromSeconds(10)), ct);
        var topicMeta = meta.Topics.FirstOrDefault(t => t.Topic == topicName);

        return new KafkaTopicConfig
        {
            RetentionMs = Lng("retention.ms", 604_800_000),
            CleanupPolicy = Str("cleanup.policy", "delete"),
            MaxMessageBytes = Lng("max.message.bytes", 1_048_576),
            MinInSyncReplicas = Int("min.insync.replicas", 1),
            CompressionType = Str("compression.type", "producer"),
            PartitionCount = topicMeta?.Partitions.Count ?? 0,
            ReplicationFactor = topicMeta?.Partitions.FirstOrDefault()?.Replicas.Length ?? 1,
        };
    }

    public async Task<IReadOnlyDictionary<string, KafkaTopicEnrichment>> GetTopicEnrichmentAsync(
        string env, IReadOnlyList<KafkaTopicSummary> topics, CancellationToken ct = default)
    {
        var withPartitions = topics.Where(t => t.PartitionCount > 0).ToList();
        if (withPartitions.Count == 0) return new Dictionary<string, KafkaTopicEnrichment>();

        // Partitions are numbered contiguously from 0, so the list can be built from
        // the partition count everyone already has — no metadata round trip needed.
        var partitions = new List<TopicPartition>();
        foreach (var t in withPartitions)
            for (var p = 0; p < t.PartitionCount; p++)
                partitions.Add(new TopicPartition(t.Name, new Partition(p)));

        log.LogDebug("kafka [{Env}]: enriching {TopicCount} topics ({PartitionCount} partitions) — 3 round trips total",
            env, withPartitions.Count, partitions.Count);

        using var admin = BuildAdmin(env);
        var timeout = new ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) };

        // One batched DescribeConfigs call for every topic's cleanup.policy (result list
        // is index-aligned with the request list — DescribeConfigsResult carries no
        // topic name of its own), and one batched ListOffsets call each for the earliest
        // and latest offset of every partition — 3 broker round trips total, regardless
        // of topic/partition count, instead of up to 2 per topic.
        var configsTask = admin.DescribeConfigsAsync(
            withPartitions.Select(t => new ConfigResource { Type = ResourceType.Topic, Name = t.Name }));
        var earliestTask = admin.ListOffsetsAsync(
            partitions.Select(tp => new TopicPartitionOffsetSpec { TopicPartition = tp, OffsetSpec = OffsetSpec.Earliest() }),
            timeout);
        var latestTask = admin.ListOffsetsAsync(
            partitions.Select(tp => new TopicPartitionOffsetSpec { TopicPartition = tp, OffsetSpec = OffsetSpec.Latest() }),
            timeout);

        await Task.WhenAll(configsTask, earliestTask, latestTask);

        var configs = configsTask.Result;
        var earliest = earliestTask.Result.ResultInfos.ToDictionary(
            r => r.TopicPartitionOffsetError.TopicPartition, r => r.TopicPartitionOffsetError.Offset.Value);
        var latest = latestTask.Result.ResultInfos.ToDictionary(
            r => r.TopicPartitionOffsetError.TopicPartition, r => r.TopicPartitionOffsetError.Offset.Value);

        var result = new Dictionary<string, KafkaTopicEnrichment>(StringComparer.Ordinal);
        for (var i = 0; i < withPartitions.Count; i++)
        {
            var t = withPartitions[i];
            var cfg = i < configs.Count ? configs[i] : null;
            var cleanupPolicy = cfg is not null && cfg.Entries.TryGetValue("cleanup.policy", out var e)
                ? e.Value : "delete";

            long messageCount = 0;
            for (var p = 0; p < t.PartitionCount; p++)
            {
                var tp = new TopicPartition(t.Name, new Partition(p));
                if (earliest.TryGetValue(tp, out var lo) && latest.TryGetValue(tp, out var hi))
                    messageCount += Math.Max(0, hi - lo);
            }

            result[t.Name] = new KafkaTopicEnrichment(cleanupPolicy, messageCount);
        }

        return result;
    }

    public async Task<IReadOnlyList<KafkaTopicConsumerGroup>> GetTopicConsumerGroupsAsync(
        string env, string topicName, CancellationToken ct = default)
    {
        log.LogDebug("kafka [{Env}]: consumer groups for '{Topic}'", env, topicName);
        using var admin = BuildAdmin(env);

        var listing = await admin.ListConsumerGroupsAsync();
        var groupIds = listing.Valid.Select(g => g.GroupId).ToList();
        if (groupIds.Count == 0) return [];

        var described = await admin.DescribeConsumerGroupsAsync(groupIds);
        var result = new List<KafkaTopicConsumerGroup>();

        foreach (var desc in described.ConsumerGroupDescriptions)
        {
            if (!desc.Members.Any(m => m.Assignment.TopicPartitions.Any(tp => tp.Topic == topicName)))
                continue;
            var lag = await ComputeGroupLagForTopicAsync(env, desc.GroupId, topicName, ct);
            result.Add(new KafkaTopicConsumerGroup
            { GroupId = desc.GroupId, State = desc.State.ToString(), TotalLag = lag });
        }
        return result;
    }

    public async IAsyncEnumerable<KafkaMessage> TailTopicAsync(
        string env, string topicName, KafkaSeekDirective directive,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        bool uncapped = false)
    {
        log.LogDebug("kafka [{Env}]: tailing '{Topic}' directive={@Directive}", env, topicName, directive);

        // Every librdkafka call this method makes — GetMetadata, QueryWatermarkOffsets,
        // OffsetsForTimes, Seek, Consume, Close — is SYNCHRONOUS and blocking (there is no
        // async Kafka API). If they ran inline on the caller's thread they would block it;
        // and in Blazor Server the caller drives this from the circuit's single-threaded
        // SynchronizationContext, so the entire UI (progress bar, Pause/Stop buttons, even
        // navigating to another tab) freezes for as long as any of these calls is in flight
        // — GetMetadata + Seek alone can block for up to ~25s before the first message.
        //
        // So the whole blocking pipeline runs on a dedicated thread-pool thread (Task.Run
        // in ProduceTailAsync) and hands messages to the caller through a bounded channel.
        // The caller here only ever does cheap, non-blocking channel reads, so the UI thread
        // stays free to render and respond to input. The bounded channel also applies natural
        // backpressure: if the UI can't keep up, the producer's WriteAsync simply awaits.
        var channel = Channel.CreateBounded<KafkaMessage>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

        // Not passing ct to Task.Run: we want the delegate to always start so its own
        // finally can close/dispose the consumer + admin handles; it observes ct itself.
        var producer = Task.Run(() =>
            ProduceTailAsync(channel.Writer, env, topicName, directive, uncapped, ct));

        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return msg;
        }
        finally
        {
            // Observe the producer so its broker handles are closed and any fatal error is
            // surfaced. ProduceTailAsync routes exceptions through the channel (not its own
            // task result) and swallows cancellation, so this only ever throws if Task.Run
            // itself was cancelled before starting — which can't happen since we don't pass ct.
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* nothing started; nothing to clean up */ }
        }
    }

    // Runs the entire blocking Kafka consume pipeline on a thread-pool thread, writing
    // decoded messages to <paramref name="writer"/>. Fatal broker errors are completed onto
    // the channel (so the reader/UI sees them), cancellation is treated as normal completion,
    // and the consumer/admin handles are always closed in the finally.
    private async Task ProduceTailAsync(
        ChannelWriter<KafkaMessage> writer,
        string env, string topicName, KafkaSeekDirective directive,
        bool uncapped, CancellationToken ct)
    {
        IAdminClient? admin = null;
        IConsumer<string?, byte[]>? consumer = null;
        try
        {
            var groupId = $"nxtui-tail-{Guid.NewGuid():N}";
            var config = factory.BuildConsumerConfig(env, groupId);

            admin = BuildAdmin(env);
            Confluent.Kafka.Metadata meta;
            try
            {
                meta = admin.GetMetadata(topicName, TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "kafka [{Env}]: could not fetch metadata for '{Topic}' — check broker connectivity/credentials", env, topicName);
                writer.TryComplete(ex);
                return;
            }

            var topicPartitions = meta.Topics
                .FirstOrDefault(t => t.Topic == topicName)?.Partitions ?? [];

            // Filter to requested partitions
            var selectedPartitions = topicPartitions
                .Where(p => directive.Partitions is null || directive.Partitions.Contains(p.PartitionId))
                .Select(p => new TopicPartition(topicName, p.PartitionId))
                .ToList();

            if (selectedPartitions.Count == 0) return;

            consumer = new ConsumerBuilder<string?, byte[]>(config).Build();
            consumer.Assign(selectedPartitions);

            // Immediately after Assign(), librdkafka hasn't finished transitioning the
            // assigned partitions to their internal fetch-active state yet — calling
            // Seek() too early intermittently throws KafkaException "Local: Erroneous
            // state" (ErrorCode.Local_State). A throwaway zero-timeout Consume() forces
            // that transition to complete before we seek.
            try { consumer.Consume(TimeSpan.Zero); } catch (ConsumeException) { /* expected: nothing to consume yet */ }

            // Seek to correct starting positions
            await SeekAsync(consumer, admin, topicName, selectedPartitions, directive, ct);

            int consumed = 0;
            // Latest is a total across all selected partitions, not per-partition — SeekAsync still
            // seeks each partition back by Latest so recent messages are available on every partition,
            // but consumption stops as soon as the combined total is reached, whichever partitions
            // it came from first.
            int cap = directive.Latest.HasValue
                ? Math.Min(directive.Latest.Value, _global.MaxFetchMessages)
                : _global.MaxFetchMessages;

            while (!ct.IsCancellationRequested && (uncapped || consumed < cap))
            {
                ConsumeResult<string?, byte[]>? cr;
                try { cr = consumer.Consume(TimeSpan.FromMilliseconds(500)); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    log.LogError(ex, "kafka [{Env}]: consume error on '{Topic}' after {Count} message(s) — stopping this tail", env, topicName, consumed);
                    break;
                }

                if (cr is null) continue;
                if (cr.IsPartitionEOF)
                {
                    // In bounded modes, EOF on all partitions means we're done
                    if (IsBounded(directive)) break;
                    continue;
                }

                // End-of-range checks
                if (directive.OffsetTo.HasValue && cr.Offset.Value > directive.OffsetTo.Value) break;
                if (directive.TimestampTo.HasValue && cr.Message.Timestamp.UtcDateTime > directive.TimestampTo.Value) break;

                var headers = new Dictionary<string, string>();
                foreach (var h in cr.Message.Headers)
                {
                    try { headers[h.Key] = System.Text.Encoding.UTF8.GetString(h.GetValueBytes() ?? []); }
                    catch { /* skip non-UTF8 */ }
                }

                var rawBytes = cr.Message.Value ?? [];
                var (json, payloadType) = pipeline.Deserialize(topicName, rawBytes);

                await writer.WriteAsync(new KafkaMessage
                {
                    Offset = cr.Offset.Value,
                    Partition = cr.Partition.Value,
                    Key = cr.Message.Key,
                    Timestamp = cr.Message.Timestamp.UtcDateTime,
                    Headers = headers,
                    JsonPayload = json,
                    PayloadType = payloadType,
                    RawSizeBytes = rawBytes.Length,
                }, ct);

                consumed++;
            }
        }
        catch (OperationCanceledException) { /* normal: paused / navigated away / superseded */ }
        catch (Exception ex)
        {
            log.LogError(ex, "kafka [{Env}]: tail producer for '{Topic}' failed", env, topicName);
            writer.TryComplete(ex);
        }
        finally
        {
            if (consumer is not null)
            {
                try { consumer.Close(); } catch { /* best-effort close */ }
                consumer.Dispose();
            }
            admin?.Dispose();
            writer.TryComplete();
        }
    }

    public Task<IReadOnlyList<KafkaPartitionStats>> GetPartitionStatsAsync(
        string env, string topicName, CancellationToken ct = default)
    {
        log.LogDebug("kafka [{Env}]: partition stats for '{Topic}'", env, topicName);

        // GetMetadata, QueryWatermarkOffsets and FetchTimestampAt (Consume) are all
        // synchronous blocking librdkafka calls — run the whole thing off the UI thread.
        return Task.Run<IReadOnlyList<KafkaPartitionStats>>(() =>
        {
            var groupId = $"nxtui-stats-{Guid.NewGuid():N}";
            var config = factory.BuildConsumerConfig(env, groupId);
            using var admin = BuildAdmin(env);
            using var consumer = new ConsumerBuilder<string?, byte[]>(config).Build();

            var meta = admin.GetMetadata(topicName, TimeSpan.FromSeconds(10));
            var parts = meta.Topics.FirstOrDefault(t => t.Topic == topicName)?.Partitions ?? [];

            var result = new List<KafkaPartitionStats>();
            foreach (var p in parts)
            {
                var tp = new TopicPartition(topicName, p.PartitionId);
                var wm = consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(5));

                DateTime? firstTs = null;
                DateTime? lastTs = null;

                // Fetch first message timestamp
                if (wm.Low.Value < wm.High.Value)
                {
                    firstTs = FetchTimestampAt(consumer, tp, wm.Low.Value);
                    lastTs = FetchTimestampAt(consumer, tp, wm.High.Value - 1);
                }

                result.Add(new KafkaPartitionStats
                {
                    Partition = p.PartitionId,
                    LowWatermark = wm.Low.Value,
                    HighWatermark = wm.High.Value,
                    FirstMessageTimestamp = firstTs,
                    LastMessageTimestamp = lastTs,
                });
            }

            return result;
        }, ct);
    }

    public Task<byte[]?> FetchRawBytesAsync(
        string env, string topicName, int partition, long offset, CancellationToken ct = default)
    {
        return Task.Run<byte[]?>(() =>
        {
            var groupId = $"nxtui-dl-{Guid.NewGuid():N}";
            var config = factory.BuildConsumerConfig(env, groupId);
            using var consumer = new ConsumerBuilder<string?, byte[]>(config).Build();

            var tp = new TopicPartition(topicName, partition);
            consumer.Assign(new TopicPartitionOffset(tp, offset));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
                    if (cr is null || cr.IsPartitionEOF) break;
                    if (cr.Offset.Value == offset) return cr.Message.Value;
                    if (cr.Offset.Value > offset) break;
                }
            }
            catch (OperationCanceledException) { }
            finally { consumer.Close(); }

            return null;
        }, ct);
    }

    public async Task<IReadOnlyList<KafkaConsumerGroupOverview>> GetAllConsumerGroupsAsync(
        string env, CancellationToken ct = default)
    {
        log.LogDebug("kafka [{Env}]: all consumer groups", env);
        using var admin = BuildAdmin(env);
        var listing = await admin.ListConsumerGroupsAsync();
        var groupIds = listing.Valid.Select(g => g.GroupId).ToList();
        if (groupIds.Count == 0) return [];

        var described = await admin.DescribeConsumerGroupsAsync(groupIds);
        var result = new List<KafkaConsumerGroupOverview>();

        foreach (var desc in described.ConsumerGroupDescriptions)
        {
            var topics = desc.Members
                .SelectMany(m => m.Assignment.TopicPartitions.Select(tp => tp.Topic))
                .Distinct().OrderBy(t => t).ToList();
            var lag = await ComputeGroupTotalLagAsync(env, desc.GroupId, ct);
            result.Add(new KafkaConsumerGroupOverview
            {
                GroupId = desc.GroupId,
                State = desc.State.ToString(),
                TopicCount = topics.Count,
                TotalLag = lag,
                Topics = topics
            });
        }
        return [.. result.OrderBy(g => g.GroupId)];
    }

    public async Task<IReadOnlyList<KafkaGroupTopicLag>> GetGroupTopicLagsAsync(
        string env, string groupId, CancellationToken ct = default)
    {
        log.LogDebug("kafka [{Env}]: lag for group '{Group}'", env, groupId);
        using var admin = BuildAdmin(env);
        var described = await admin.DescribeConsumerGroupsAsync([groupId]);
        var desc = described.ConsumerGroupDescriptions.First();

        var topicPartitions = desc.Members
            .SelectMany(m => m.Assignment.TopicPartitions)
            .GroupBy(tp => tp.Topic)
            .Select(g => (Topic: g.Key, PartitionCount: g.Count()))
            .ToList();

        var result = new List<KafkaGroupTopicLag>();
        foreach (var (topic, partCount) in topicPartitions)
        {
            var lag = await ComputeGroupLagForTopicAsync(env, groupId, topic, ct);
            result.Add(new KafkaGroupTopicLag { TopicName = topic, Partitions = partCount, Lag = lag });
        }
        return [.. result.OrderBy(x => x.TopicName)];
    }

    // ── IKafkaAdmin ──────────────────────────────────────────────────────────

    public async Task DeleteTopicAsync(string env, string topicName, CancellationToken ct = default)
    {
        log.LogInformation("kafka [{Env}]: deleting topic '{Topic}'", env, topicName);
        using var admin = BuildAdmin(env);
        await admin.DeleteTopicsAsync([topicName]);
    }

    public async Task SetTopicRetentionAsync(string env, string topicName, long retentionMs, CancellationToken ct = default)
    {
        log.LogInformation("kafka [{Env}]: retention '{Topic}' → {Ms}ms", env, topicName, retentionMs);
        using var admin = BuildAdmin(env);
        await admin.AlterConfigsAsync(new Dictionary<ConfigResource, List<ConfigEntry>>
        {
            [new ConfigResource { Type = ResourceType.Topic, Name = topicName }] =
                [new ConfigEntry { Name = "retention.ms", Value = retentionMs.ToString() }]
        });
    }

    public async Task DeleteConsumerGroupAsync(string env, string groupId, CancellationToken ct = default)
    {
        log.LogInformation("kafka [{Env}]: deleting group '{Group}'", env, groupId);
        using var admin = BuildAdmin(env);
        await admin.DeleteGroupsAsync([groupId]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IAdminClient BuildAdmin(string env) =>
        new AdminClientBuilder(factory.GetAdminConfig(env)).Build();

    private static bool IsBounded(KafkaSeekDirective d) =>
        d.OffsetTo.HasValue || d.TimestampTo.HasValue || d.Latest.HasValue;

    private static async Task SeekAsync(
        IConsumer<string?, byte[]> consumer,
        IAdminClient admin,
        string topicName,
        List<TopicPartition> partitions,
        KafkaSeekDirective directive,
        CancellationToken ct)
    {
        if (directive.Latest.HasValue)
        {
            // Seek each partition to (high - Latest)
            foreach (var tp in partitions)
            {
                var wm = consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(5));
                var startOffset = Math.Max(wm.Low.Value, wm.High.Value - directive.Latest.Value);
                await SeekWithRetryAsync(consumer, new TopicPartitionOffset(tp, startOffset), ct);
            }
            return;
        }

        if (directive.TimestampFrom.HasValue)
        {
            var tpos = partitions.Select(tp =>
                new TopicPartitionTimestamp(tp, new Timestamp(directive.TimestampFrom.Value))).ToList();
            var offsets = consumer.OffsetsForTimes(tpos, TimeSpan.FromSeconds(10));
            foreach (var tpo in offsets)
                await SeekWithRetryAsync(consumer, tpo.Offset.IsSpecial
                    ? new TopicPartitionOffset(tpo.TopicPartition, Offset.Beginning)
                    : tpo, ct);
            return;
        }

        if (directive.OffsetFrom.HasValue)
        {
            foreach (var tp in partitions)
                await SeekWithRetryAsync(consumer, new TopicPartitionOffset(tp, directive.OffsetFrom.Value), ct);
            return;
        }

        // Default: beginning
        foreach (var tp in partitions)
            await SeekWithRetryAsync(consumer, new TopicPartitionOffset(tp, Offset.Beginning), ct);
    }

    /// <summary>
    /// Wraps IConsumer.Seek() with retries against the Assign()-then-Seek() race: right after
    /// Assign(), librdkafka hasn't always finished transitioning the partition to its internal
    /// fetch-active state, and Seek() intermittently throws KafkaException "Local: Erroneous
    /// state" (ErrorCode.Local_State) until it has. A single warm-up Consume() call in
    /// TailTopicAsync usually avoids this, but isn't a hard guarantee (the transition is
    /// asynchronous inside librdkafka) — retrying specifically on this error code is the
    /// robust fix, since it keeps trying until the partition is actually ready instead of
    /// gambling on a fixed delay being long enough.
    /// </summary>
    private static async Task SeekWithRetryAsync(
        IConsumer<string?, byte[]> consumer, TopicPartitionOffset tpo, CancellationToken ct)
    {
        const int maxAttempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                consumer.Seek(tpo);
                return;
            }
            catch (KafkaException ex) when (ex.Error.Code == ErrorCode.Local_State && attempt < maxAttempts)
            {
                await Task.Delay(50, ct);
            }
        }
    }

    private static DateTime? FetchTimestampAt(
        IConsumer<string?, byte[]> consumer, TopicPartition tp, long offset)
    {
        try
        {
            consumer.Assign(new TopicPartitionOffset(tp, offset));
            var cr = consumer.Consume(TimeSpan.FromSeconds(3));
            return cr?.Message.Timestamp.UtcDateTime;
        }
        catch { return null; }
    }

    private Task<long> ComputeGroupLagForTopicAsync(
        string env, string groupId, string topicName, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                var cfg = factory.BuildConsumerConfig(env, $"{groupId}__lag_{Guid.NewGuid():N}");
                using var consumer = new ConsumerBuilder<Ignore, Ignore>(cfg).Build();
                using var admin = BuildAdmin(env);
                var meta = admin.GetMetadata(topicName, TimeSpan.FromSeconds(10));
                var topicMeta = meta.Topics.FirstOrDefault(t => t.Topic == topicName);
                if (topicMeta is null) return 0L;

                var tps = topicMeta.Partitions
                    .Select(p => new TopicPartition(topicName, p.PartitionId)).ToList();
                var committed = consumer.Committed(tps, TimeSpan.FromSeconds(10));
                long lag = 0;
                foreach (var tpo in committed)
                {
                    var hi = consumer.QueryWatermarkOffsets(tpo.TopicPartition, TimeSpan.FromSeconds(5));
                    lag += Math.Max(0, hi.High.Value - (tpo.Offset.IsSpecial ? 0 : tpo.Offset.Value));
                }
                return lag;
            }
            catch { return 0L; }
        }, ct);

    private async Task<long> ComputeGroupTotalLagAsync(string env, string groupId, CancellationToken ct)
    {
        using var admin = BuildAdmin(env);
        // GetMetadata blocks synchronously — offload it off the UI thread.
        var meta = await Task.Run(() => admin.GetMetadata(TimeSpan.FromSeconds(10)), ct);
        var topics = meta.Topics.Where(t => !t.Topic.StartsWith("__")).Select(t => t.Topic).ToList();
        long total = 0;
        foreach (var topic in topics)
            total += await ComputeGroupLagForTopicAsync(env, groupId, topic, ct);
        return total;
    }
}
