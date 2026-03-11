using BulkUploader.Core;
using FluentAssertions;
using Xunit;

namespace BulkUploader.Tests;

public sealed class BatchSizeTunerTests
{
    [Fact]
    public void InitialBatchSize_IsClampedToRange()
    {
        new BatchSizeTuner(initial: 5, min: 10, max: 100).BatchSize.Should().Be(10);
        new BatchSizeTuner(initial: 200, min: 10, max: 100).BatchSize.Should().Be(100);
        new BatchSizeTuner(initial: 50, min: 10, max: 100).BatchSize.Should().Be(50);
    }

    [Fact]
    public void FirstMeasurement_DoesNotChangeBatchSize()
    {
        var tuner = new BatchSizeTuner(initial: 100, min: 10, max: 1000);
        var before = tuner.BatchSize;

        tuner.RecordBatch(100, 50);

        tuner.BatchSize.Should().Be(before); // no change on first measurement
    }

    [Fact]
    public void ImprovedThroughput_KeepsDirection()
    {
        var tuner = new BatchSizeTuner(initial: 100, min: 10, max: 10_000, stepFraction: 0.1);
        tuner.RecordBatch(100, 100); // baseline: 1.0 rec/ms
        var sizeAfterFirst = tuner.BatchSize;

        tuner.RecordBatch(sizeAfterFirst, 50); // improved: >1 rec/ms

        // Should keep growing (same direction)
        tuner.BatchSize.Should().BeGreaterThan(sizeAfterFirst);
        tuner.Snapshot().Direction.Should().Be(1);
    }

    [Fact]
    public void WorsenedThroughput_ReversesDirection()
    {
        var tuner = new BatchSizeTuner(initial: 100, min: 10, max: 10_000, stepFraction: 0.1);
        tuner.RecordBatch(100, 100); // baseline
        var sizeAfterFirst = tuner.BatchSize;

        tuner.RecordBatch(sizeAfterFirst, 300); // much worse

        tuner.Snapshot().Direction.Should().Be(-1);
    }

    [Fact]
    public void Override_ResetsTunerAndResumesHillClimbing()
    {
        var tuner = new BatchSizeTuner(initial: 100, min: 10, max: 10_000);
        tuner.RecordBatch(100, 100);
        tuner.RecordBatch(200, 200);

        tuner.Override(500);

        tuner.BatchSize.Should().Be(500);
        // After override, next measurement becomes new baseline — no direction change yet.
        tuner.RecordBatch(500, 250);
        tuner.BatchSize.Should().Be(500); // first measurement, no nudge
    }

    [Fact]
    public void BatchSize_StaysWithinMinMax()
    {
        var tuner = new BatchSizeTuner(initial: 50, min: 50, max: 50, stepFraction: 0.5);
        tuner.RecordBatch(50, 50);
        tuner.RecordBatch(50, 50);

        tuner.BatchSize.Should().Be(50);
    }

    [Fact]
    public void InvalidArguments_ThrowArgumentOutOfRangeException()
    {
        Action minZero  = () => new BatchSizeTuner(min: 0);
        Action maxLtMin = () => new BatchSizeTuner(min: 100, max: 50);

        minZero.Should().Throw<ArgumentOutOfRangeException>();
        maxLtMin.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DeadBand_PreventsThrashingOnNoise()
    {
        // 4% change — within 5% dead-band — should not reverse.
        var tuner = new BatchSizeTuner(initial: 100, min: 10, max: 10_000,
            stepFraction: 0.1, deadBandFraction: 0.05);

        tuner.RecordBatch(100, 100); // baseline 1.0 rec/ms
        var sizeBefore = tuner.BatchSize;
        var dirBefore  = tuner.Snapshot().Direction;

        tuner.RecordBatch(104, 100); // 1.04 rec/ms — 4% better, within dead-band

        tuner.Snapshot().Direction.Should().Be(dirBefore); // should NOT have reversed
    }
}
