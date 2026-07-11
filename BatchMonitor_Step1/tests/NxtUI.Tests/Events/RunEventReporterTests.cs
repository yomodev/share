using AwesomeAssertions;
using NxtUI.Core.Events;

namespace NxtUI.Tests.Events;

/// <summary>Exercises RunEventReporter against FakeEventSink — also doubles as the demo
/// that the fake pair composes correctly for future producer/aggregator tests.</summary>
public class RunEventReporterTests
{
    [Fact]
    public async Task ConnectAsync_emits_a_Connect_event_with_no_correlation()
    {
        var sink = new FakeEventSink();
        var reporter = new RunEventReporter(sink, runId: "RUN-1", service: "Loader", server: "srv-01", processId: 42);

        await reporter.ConnectAsync("package started", TestContext.Current.CancellationToken);

        var evt = sink.Written.Should().ContainSingle().Subject;
        evt.Kind.Should().Be(EventKind.Connect);
        evt.RunId.Should().Be("RUN-1");
        evt.Service.Should().Be("Loader");
        evt.Server.Should().Be("srv-01");
        evt.ProcessId.Should().Be(42);
        evt.CorrelationId.Should().BeNull();
        evt.Info.Should().Be("package started");
    }

    [Fact]
    public async Task ProduceAsync_and_ConsumeAsync_carry_target_and_record_count()
    {
        var sink = new FakeEventSink();
        var reporter = new RunEventReporter(sink, "RUN-1", "Extract", pipeline: "DF Extract");

        await reporter.ConsumeAsync(@"\\share\in\cust.csv", correlationId: "BATCH-1", recordCount: 50_000, source: "Flat File Src", ct: TestContext.Current.CancellationToken);
        await reporter.ProduceAsync("stg.Customer", correlationId: "BATCH-1", recordCount: 50_000, source: "OLE DB Dest", ct: TestContext.Current.CancellationToken);

        sink.Written.Should().HaveCount(2);
        sink.Written[0].Kind.Should().Be(EventKind.Consume);
        sink.Written[0].Target.Should().Be(@"\\share\in\cust.csv");
        sink.Written[0].Pipeline.Should().Be("DF Extract");
        sink.Written[1].Kind.Should().Be(EventKind.Produce);
        sink.Written[1].Target.Should().Be("stg.Customer");
        sink.Written[1].RecordCount.Should().Be(50_000);
    }

    [Fact]
    public async Task BeginActivity_emits_one_Process_span_on_dispose_with_start_before_finish()
    {
        var sink = new FakeEventSink();
        var reporter = new RunEventReporter(sink, "RUN-1", "Transform");

        await using (var act = reporter.BeginActivity("DFT Transform", correlationId: "BATCH-1"))
        {
            act.RecordCount = 4980;
        }

        var evt = sink.Written.Should().ContainSingle().Subject;
        evt.Kind.Should().Be(EventKind.Process);
        evt.Status.Should().Be(EventStatus.Success);
        evt.RecordCount.Should().Be(4980);
        evt.FinishUtc.Should().NotBeNull();
        evt.FinishUtc!.Value.Should().BeOnOrAfter(evt.TimestampUtc);
    }

    [Fact]
    public async Task BeginActivity_Fail_marks_the_span_Failed_with_the_error_message()
    {
        var sink = new FakeEventSink();
        var reporter = new RunEventReporter(sink, "RUN-1", "Load");

        await using (var act = reporter.BeginActivity("DFT Load", correlationId: "BATCH-1", target: "dbo.Customer"))
        {
            act.Fail("PK violation on CustomerId=8842");
        }

        var evt = sink.Written.Should().ContainSingle().Subject;
        evt.Status.Should().Be(EventStatus.Failed);
        evt.Error.Should().Be("PK violation on CustomerId=8842");
        evt.Target.Should().Be("dbo.Customer");
    }

    [Fact]
    public async Task ErrorAsync_emits_an_Error_event()
    {
        var sink = new FakeEventSink();
        var reporter = new RunEventReporter(sink, "RUN-1", "Load");

        await reporter.ErrorAsync("boom", correlationId: "BATCH-1", source: "OLE DB Dest", target: "dbo.Customer", TestContext.Current.CancellationToken);

        var evt = sink.Written.Should().ContainSingle().Subject;
        evt.Kind.Should().Be(EventKind.Error);
        evt.Status.Should().Be(EventStatus.Failed);
        evt.Error.Should().Be("boom");
    }

    [Fact]
    public async Task Sink_throwing_propagates_to_the_caller()
    {
        var sink = new FakeEventSink { ThrowOnNextWrite = new InvalidOperationException("db down") };
        var reporter = new RunEventReporter(sink, "RUN-1", "Loader");

        var act = async () => await reporter.ConnectAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("db down");
        sink.WriteCallCount.Should().Be(1);
        sink.Written.Should().BeEmpty();
    }
}
