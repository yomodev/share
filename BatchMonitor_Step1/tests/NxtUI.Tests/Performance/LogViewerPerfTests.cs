using AwesomeAssertions;
using NxtUI.Web.Services;
using System.Diagnostics;

namespace NxtUI.Tests.Performance;

/// <summary>
/// Exercises ILogViewerService (ReadAllAsync / ReadDelta) with real temp files.
/// Tests both correctness (offset tracking, incremental reads) and timing
/// so you can spot slow I/O or encoding overhead when pointed at real paths.
/// </summary>
public sealed class LogViewerPerfTests
{
    private static readonly LogViewerService Viewer = new();

    // ── ReadAllAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAllAsync_returns_full_content_and_correct_offset()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            var lines = Enumerable.Range(1, 1_000)
                .Select(i => $"2024-01-01 00:{i / 60:D2}:{i % 60:D2}|INFO|Svc|{i}|1|line {i}|Caller")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, ct);

            var (text, offset) = await Viewer.ReadAllAsync(path, ct);

            text.Should().Contain("line 1");
            text.Should().Contain("line 1000");
            offset.Should().BeGreaterThan(0);
            offset.Should().Be(new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadAllAsync_empty_file_returns_empty_string_at_offset_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, string.Empty, ct);
            var (text, offset) = await Viewer.ReadAllAsync(path, ct);
            text.Should().BeEmpty();
            offset.Should().Be(0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadAllAsync_large_file_timing()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            var lines = Enumerable.Range(1, 50_000)
                .Select(i => $"2024-01-01 {i / 3600 % 24:D2}:{i / 60 % 60:D2}:{i % 60:D2}|INFO|Svc-{i % 8}|{i}|{i % 20}|Payload text line {i}|Method.Call")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, ct);

            var fileMb = new FileInfo(path).Length / 1_048_576.0;
            var sw = Stopwatch.StartNew();
            var (text, offset) = await Viewer.ReadAllAsync(path, ct);
            sw.Stop();

            text.Length.Should().BeGreaterThan(0);
            offset.Should().Be(new FileInfo(path).Length);

            TestContext.Current.SendDiagnosticMessage(
                $"50k lines ({fileMb:F1} MB): ReadAllAsync in {sw.Elapsed.TotalMilliseconds:F0}ms " +
                $"({fileMb / sw.Elapsed.TotalSeconds:F1} MB/s)");
        }
        finally { File.Delete(path); }
    }

    // ── ReadDelta ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadDelta_from_zero_returns_full_content()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\n", ct);
            var (text, offset) = await Viewer.ReadDeltaAsync(path, 0, ct);
            text.Should().Contain("alpha").And.Contain("gamma");
            offset.Should().Be(new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadDelta_after_ReadAll_returns_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "line1\nline2\nline3\n", ct);
            var (_, offset) = await Viewer.ReadAllAsync(path, ct);

            var (delta, newOffset) = await Viewer.ReadDeltaAsync(path, offset, ct);
            delta.Should().BeEmpty();
            newOffset.Should().Be(offset);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadDelta_detects_appended_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "line1\nline2\n", ct);
            var (_, offset) = await Viewer.ReadAllAsync(path, ct);

            await File.AppendAllTextAsync(path, "line3\nline4\n", ct);
            var (delta, newOffset) = await Viewer.ReadDeltaAsync(path, offset, ct);

            delta.Should().Contain("line3").And.Contain("line4");
            delta.Should().NotContain("line1");
            newOffset.Should().BeGreaterThan(offset);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadDelta_repeated_calls_are_incremental()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            // Simulate a live log file being tailed with three successive appends.
            await File.WriteAllTextAsync(path, "batch1-line1\n", ct);
            var (_, off1) = await Viewer.ReadAllAsync(path, ct);

            await File.AppendAllTextAsync(path, "batch2-line1\nbatch2-line2\n", ct);
            var (delta2, off2) = await Viewer.ReadDeltaAsync(path, off1, ct);
            delta2.Should().Contain("batch2-line1").And.Contain("batch2-line2");
            delta2.Should().NotContain("batch1");

            await File.AppendAllTextAsync(path, "batch3-line1\n", ct);
            var (delta3, off3) = await Viewer.ReadDeltaAsync(path, off2, ct);
            delta3.Should().Contain("batch3-line1");
            delta3.Should().NotContain("batch2");

            off3.Should().BeGreaterThan(off2).And.BeGreaterThan(off1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadAllAsync_nonexistent_file_throws_or_returns_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        // FileStream(FileMode.Open) throws FileNotFoundException for missing files.
        // This test documents the contract so regressions are visible.
        var act = async () => await Viewer.ReadAllAsync(path);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ── Log parsing timing ─────────────────────────────────────────────────

    [Fact]
    public async Task ParseLine_throughput_for_1k_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            var lines = Enumerable.Range(1, 1_000)
                .Select(i => $"2024-06-01 {i / 3600 % 24:D2}:{i / 60 % 60:D2}:{i % 60:D2}.{i % 1000:D3}|WARN|Svc|{i}|42|Message text {i}|Ns.Class.Method")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, ct);

            var sw = Stopwatch.StartNew();
            var (_, offset) = await Viewer.ReadAllAsync(path, ct);
            sw.Stop();

            offset.Should().BeGreaterThan(0);
            TestContext.Current.SendDiagnosticMessage(
                $"1k lines: ReadAllAsync in {sw.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally { File.Delete(path); }
    }
}
