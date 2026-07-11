using AwesomeAssertions;
using NxtUI.Core.Events;

namespace NxtUI.Tests.Events;

public class PerformanceEventBridgeTests
{
    private static RunEvent Base(EventKind kind, string? correlationId = "chunk1") => new()
    {
        RunId = "RUN-1",
        EventId = "evt-1",
        CorrelationId = correlationId,
        Service = "Loader",
        Server = "srv-01",
        ProcessId = 42,
        Pipeline = "DF Load",
        Kind = kind,
        Source = "OLE DB Dest",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        FinishUtc = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc),
        Status = EventStatus.Success,
        RecordCount = 100,
    };

    [Fact]
    public void Process_event_with_correlation_maps_to_PerformanceEvent()
    {
        var pe = PerformanceEventBridge.ToPerformanceEvent(Base(EventKind.Process));

        pe.Should().NotBeNull();
        pe!.Name.Should().Be("chunk1");
        pe.Service.Should().Be("Loader");
        pe.Pipeline.Should().Be("DF Load");
        pe.Server.Should().Be("srv-01");
        pe.ProcessId.Should().Be(42);
        pe.RecordCount.Should().Be(100);
        pe.Finish.Should().Be(new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));
        pe.IsDone.Should().BeTrue();
    }

    [Theory]
    [InlineData(EventKind.Produce)]
    [InlineData(EventKind.Consume)]
    public void Produce_and_consume_events_with_correlation_also_map(EventKind kind)
    {
        PerformanceEventBridge.ToPerformanceEvent(Base(kind)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(EventKind.Connect)]
    [InlineData(EventKind.Disconnect)]
    [InlineData(EventKind.Info)]
    public void Lifecycle_and_annotation_events_do_not_map(EventKind kind)
    {
        PerformanceEventBridge.ToPerformanceEvent(Base(kind)).Should().BeNull();
    }

    [Fact]
    public void Process_event_without_correlation_id_does_not_map()
    {
        PerformanceEventBridge.ToPerformanceEvent(Base(EventKind.Process, correlationId: null)).Should().BeNull();
    }

    [Fact]
    public void Error_carries_through_and_marks_the_event_as_errored()
    {
        var evt = new RunEvent
        {
            RunId = "RUN-1",
            EventId = "evt-1",
            CorrelationId = "chunk1",
            Service = "Loader",
            Server = "srv-01",
            ProcessId = 42,
            Pipeline = "DF Load",
            Kind = EventKind.Process,
            Source = "OLE DB Dest",
            TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = EventStatus.Failed,
            Error = "PK violation",
        };
        var pe = PerformanceEventBridge.ToPerformanceEvent(evt);

        pe.Should().NotBeNull();
        pe!.Error.Should().Be("PK violation");
        pe.IsError.Should().BeTrue();
        pe.IsDone.Should().BeFalse(); // no FinishUtc on this variant
    }
}
