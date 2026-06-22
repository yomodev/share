namespace NxtUI.Configuration;

public class LogPathSettings
{
    public const string SectionName = "LogPaths";

    /// <summary>
    /// Ordered list of UNC path templates. Placeholders:
    ///   {server}  — HostName from heartbeat
    ///   {service} — ServiceName from heartbeat
    ///   {pid}     — ProcessId from heartbeat
    ///   {env}     — environment Id (e.g. UAT1)
    ///   {date}    — CreatedDateTime as yyyy-MM-dd
    ///   {date-1}  — CreatedDateTime minus one day as yyyy-MM-dd
    /// A segment may contain * which is expanded via Directory.GetDirectories.
    /// First template whose path resolves wins.
    /// </summary>
    public List<string> Templates { get; set; } = new();

    /// <summary>
    /// Fixed name of the file inside the resolved folder that contains the
    /// process memory metrics lines (see MetricsLogParser). The folder is unique
    /// per service/PID, so this filename is constant across services.
    /// </summary>
    public string MetricsFileName { get; set; } = string.Empty;
}
