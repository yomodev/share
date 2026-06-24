namespace NxtUI.Configuration;

public class LogPathSettings
{
    public const string SectionName = "Logs";

    /// <summary>
    /// Ordered list of UNC path templates for locating per-service metrics folders. Placeholders:
    ///   {server}  — HostName from heartbeat
    ///   {service} — ServiceName from heartbeat
    ///   {pid}     — ProcessId from heartbeat
    ///   {env}     — environment Id (e.g. UAT1)
    ///   {date}    — CreatedDateTime as yyyy-MM-dd
    ///   {date-1}  — CreatedDateTime minus one day as yyyy-MM-dd
    /// A segment may contain * which is expanded via Directory.GetDirectories.
    /// First template whose path resolves wins.
    /// </summary>
    public List<string> ServiceTemplates { get; set; } = new();

    /// <summary>
    /// Fixed name of the file inside the resolved folder that contains the
    /// process memory metrics lines (see MetricsLogParser). The folder is unique
    /// per service/PID, so this filename is constant across services.
    /// </summary>
    public string MetricsFileName { get; set; } = string.Empty;

    /// <summary>How often (seconds) to re-read the metrics file for new lines.</summary>
    public int MetricsIntervalSeconds { get; set; } = 90;

    /// <summary>
    /// UNC path template for the Log Browser base folder per server.
    /// Placeholder: {server} — server hostname.
    /// Example: "\\{server}\Shared\bau\logs"
    /// </summary>
    public string LogsFolder { get; set; } = string.Empty;

    /// <summary>
    /// List of server hostnames to enumerate in the Log Browser tree.
    /// If empty, the browser falls back to hosts seen in live heartbeats.
    /// </summary>
    public List<string> Servers { get; set; } = new();

    /// <summary>How often (seconds) the Log Browser polls the selected folder and its parent.</summary>
    public int FolderScanIntervalSeconds { get; set; } = 30;

    /// <summary>Minimum seconds between two polls of the same folder (throttle on click).</summary>
    public int FolderScanMinIntervalSeconds { get; set; } = 10;
}
