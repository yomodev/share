using AwesomeAssertions;
using NxtUI.Core.Events;
using NxtUI.Core.Filtering;
using NxtUI.Core.Models;
using NxtUI.Core.Services;

namespace NxtUI.Tests.Events;

/// <summary>A minimal IRunService stub — only GetRunEventsAsync is exercised by PerformanceEventSource.</summary>
file sealed class FakeRunService : IRunService
{
    public List<PerformanceEvent> Events { get; set; } = [];
    public DateTime? LastFrom { get; private set; }

    public Task<List<PerformanceEvent>> GetRunEventsAsync(string env, string runId, DateTime from, CancellationToken ct = default)
    {
        LastFrom = from;
        return Task.FromResult(Events.Where(e => (e.LastUpdate == default ? (e.Finish ?? e.Start) : e.LastUpdate) >= from).ToList());
    }

    public Task<List<RunSummary>> GetRunsAsync(string env, DateTime before, int count, RunFilter? filter = null, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<bool> CancelRunAsync(string env, string runId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RunDetails> GetRunDetailsAsync(string env, string runId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Topology> GetRunTopologyAsync(string env, string runId, CancellationToken ct = default) => throw new NotImplementedException();
}

public class PerformanceEventSourceTests
{
    private static PerformanceEvent Evt(string name, DateTime start, DateTime? finish = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Service = "Loader",
        Pipeline = "DF Load",
        Server = "srv-01",
        ProcessId = 1,
        Start = start,
        Finish = finish,
        LastUpdate = finish ?? start,
    };

    [Fact]
    public async Task First_poll_uses_run_start_and_maps_every_event_as_a_Process_span()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fake = new FakeRunService { Events = [Evt("chunk1", t0, t0.AddSeconds(5))] };
        var source = new PerformanceEventSource(fake, "DEV1", t0);

        var batch = await source.PollAsync("RUN-1", cursor: null);

        fake.LastFrom.Should().Be(t0);
        batch.Events.Should().ContainSingle();
        var e = batch.Events[0];
        e.Kind.Should().Be(EventKind.Process);
        e.CorrelationId.Should().Be("chunk1");
        e.Status.Should().Be(EventStatus.Success);
        batch.Cursor.LastTimestampUtc.Should().Be(t0.AddSeconds(5));
    }

    [Fact]
    public async Task Second_poll_resumes_from_the_returned_cursor()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fake = new FakeRunService { Events = [Evt("chunk1", t0, t0.AddSeconds(5))] };
        var source = new PerformanceEventSource(fake, "DEV1", t0);

        var first = await source.PollAsync("RUN-1", null);
        fake.Events.Add(Evt("chunk2", t0.AddSeconds(10), t0.AddSeconds(12)));

        var second = await source.PollAsync("RUN-1", first.Cursor);

        fake.LastFrom.Should().Be(first.Cursor.LastTimestampUtc);
        second.Events.Should().Contain(e => e.CorrelationId == "chunk2");
    }

    [Fact]
    public async Task Empty_result_keeps_the_cursor_unchanged()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fake = new FakeRunService { Events = [] };
        var source = new PerformanceEventSource(fake, "DEV1", t0);

        var cursorIn = new EventCursor(LastTimestampUtc: t0.AddMinutes(1));
        var batch = await source.PollAsync("RUN-1", cursorIn);

        batch.Events.Should().BeEmpty();
        batch.Cursor.Should().Be(cursorIn);
    }

    [Fact]
    public async Task Failed_event_maps_to_Failed_status()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var errored = Evt("chunk1", t0, t0.AddSeconds(1));
        errored.Error = "boom";
        var fake = new FakeRunService { Events = [errored] };
        var source = new PerformanceEventSource(fake, "DEV1", t0);

        var batch = await source.PollAsync("RUN-1", null);

        batch.Events.Single().Status.Should().Be(EventStatus.Failed);
        batch.Events.Single().Error.Should().Be("boom");
    }
}
