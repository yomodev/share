using AwesomeAssertions;
using NxtUI.Core.Events;

namespace NxtUI.Tests.Events;

/// <summary>Proves FakeEventSource honors the same resumable-cursor contract as a real
/// IEventSource, so it's safe to hand to other tests (e.g. a future multi-source
/// aggregator) in place of SqlRunEventSource/SsisDbEventSource/PerformanceEventSource.</summary>
public class FakeEventSourceTests
{
    private static RunEvent Evt(string correlationId) => new()
    {
        RunId = "RUN-1",
        EventId = Guid.NewGuid().ToString("N"),
        CorrelationId = correlationId,
        Service = "Loader",
        Kind = EventKind.Process,
        TimestampUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task First_poll_with_null_cursor_returns_everything_enqueued_so_far()
    {
        var source = new FakeEventSource();
        source.Enqueue(Evt("a"), Evt("b"));

        var batch = await source.PollAsync("RUN-1", cursor: null, ct: TestContext.Current.CancellationToken);

        batch.Events.Should().HaveCount(2);
        batch.Cursor.LastId.Should().Be(2);
    }

    [Fact]
    public async Task Second_poll_only_returns_events_enqueued_after_the_first()
    {
        var source = new FakeEventSource();
        source.Enqueue(Evt("a"));
        var first = await source.PollAsync("RUN-1", null, TestContext.Current.CancellationToken);

        source.Enqueue(Evt("b"), Evt("c"));
        var second = await source.PollAsync("RUN-1", first.Cursor, TestContext.Current.CancellationToken);

        second.Events.Should().HaveCount(2);
        second.Events.Select(e => e.CorrelationId).Should().Equal("b", "c");
        second.Cursor.LastId.Should().Be(3);
    }

    [Fact]
    public async Task Polling_with_no_new_events_returns_an_empty_batch_and_unchanged_cursor()
    {
        var source = new FakeEventSource();
        source.Enqueue(Evt("a"));
        var first = await source.PollAsync("RUN-1", null, TestContext.Current.CancellationToken);

        var second = await source.PollAsync("RUN-1", first.Cursor, TestContext.Current.CancellationToken);

        second.Events.Should().BeEmpty();
        second.Cursor.Should().Be(first.Cursor);
    }

    [Fact]
    public async Task PollCount_tracks_how_many_times_it_was_polled()
    {
        var source = new FakeEventSource();
        await source.PollAsync("RUN-1", null, TestContext.Current.CancellationToken);
        await source.PollAsync("RUN-1", null, TestContext.Current.CancellationToken);

        source.PollCount.Should().Be(2);
    }

    [Fact]
    public async Task ThrowOnNextPoll_throws_once_then_recovers()
    {
        var source = new FakeEventSource
        {
            ThrowOnNextPoll = new TimeoutException("broker unreachable")
        };

        var act = async () => await source.PollAsync("RUN-1", null, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<TimeoutException>();

        // Second call should succeed — the fault was one-shot.
        var batch = await source.PollAsync("RUN-1", null, TestContext.Current.CancellationToken);
        batch.Events.Should().BeEmpty();
    }

    [Fact]
    public void Name_defaults_to_fake_but_can_be_customized()
    {
        new FakeEventSource().Name.Should().Be("fake");
        new FakeEventSource("my-source").Name.Should().Be("my-source");
    }
}
