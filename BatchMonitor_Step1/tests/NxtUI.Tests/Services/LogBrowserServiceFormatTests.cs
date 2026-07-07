using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NxtUI.Core.Configuration;
using NxtUI.Core.Filtering;
using NxtUI.Web.Services;

namespace NxtUI.Tests.Services;

/// <summary>
/// Verifies LogBrowserService.SearchAsync's content filter parses log lines using the
/// same configurable "Logs:Formats" templates as the interactive LogViewer (log-viewer-parser.js
/// compileFormat), instead of assuming one fixed pipe-delimited layout.
/// </summary>
public sealed class LogBrowserServiceFormatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nxtui-lb-tests-" + Guid.NewGuid());

    private LogBrowserService CreateService(string[] formats)
    {
        var settings = new LogPathSettings { RootFolder = _root };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(formats.Select((f, i) =>
                new KeyValuePair<string, string?>($"Logs:Formats:{i}", f)))
            .Build();
        return new LogBrowserService(Options.Create(settings), config, NullLogger<LogBrowserService>.Instance);
    }

    private string WriteLogFile(string relativeDir, string fileName, string content)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task SearchAsync_MatchesCallerField_UsingConfiguredFormat()
    {
        // Format matches appsettings.json's real template:
        // {timestamp}|{level}|{host}|{pid}|{thread}|{message}|{caller}{*}{$}
        var svc = CreateService(["{timestamp}|{level}|{host}|{pid}|{thread}|{message}|{caller}{*}{$}"]);
        WriteLogFile("", "a.log", "2024-01-15 10:00:00|INFO|host1|111|1|hello world|OrderService.Process\n");
        WriteLogFile("", "b.log", "2024-01-15 10:00:00|INFO|host1|111|1|hello world|PaymentService.Charge\n");

        var filter = LogLineParser().Parse("caller:OrderService", useUtc: true);
        var results = new List<string>();
        await foreach (var entry in svc.SearchAsync(["local"], "", "*.log", filter, CancellationToken.None))
            results.Add(entry.FileName);

        results.Should().Contain("a.log");
        results.Should().NotContain("b.log");
    }

    [Fact]
    public async Task SearchAsync_MatchesThreadField_UsingConfiguredFormat()
    {
        var svc = CreateService(["{timestamp}|{level}|{host}|{pid}|{thread}|{message}|{caller}{*}{$}"]);
        WriteLogFile("", "thread5.log", "2024-01-15 10:00:00|INFO|host1|111|5|hello|Caller.Method\n");
        WriteLogFile("", "thread9.log", "2024-01-15 10:00:00|INFO|host1|111|9|hello|Caller.Method\n");

        var filter = LogLineParser().Parse("thread:5", useUtc: true);
        var results = new List<string>();
        await foreach (var entry in svc.SearchAsync(["local"], "", "*.log", filter, CancellationToken.None))
            results.Add(entry.FileName);

        results.Should().Contain("thread5.log");
        results.Should().NotContain("thread9.log");
    }

    [Fact]
    public async Task SearchAsync_FallsBackToLegacyPipeFormat_WhenNoFormatsConfigured()
    {
        var svc = CreateService([]);
        WriteLogFile("", "legacy.log", "2024-01-15 10:00:00|INFO|host1|111|1|hello world|Legacy.Caller\n");

        var filter = LogLineParser().Parse("level:INFO", useUtc: true);
        var results = new List<string>();
        await foreach (var entry in svc.SearchAsync(["local"], "", "*.log", filter, CancellationToken.None))
            results.Add(entry.FileName);

        results.Should().Contain("legacy.log");
    }

    private static FilterParser LogLineParser() => new(
        searchableFields: ["Level", "Machine", "Message"],
        aliases: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ts"] = "Timestamp",
            ["timestamp"] = "Timestamp",
            ["level"] = "Level",
            ["machine"] = "Machine",
            ["pid"] = "Pid",
            ["thread"] = "ThreadId",
            ["tid"] = "ThreadId",
            ["msg"] = "Message",
            ["message"] = "Message",
            ["caller"] = "Caller",
        });

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
