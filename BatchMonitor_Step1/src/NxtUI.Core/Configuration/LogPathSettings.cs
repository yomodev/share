namespace NxtUI.Core.Configuration;

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
    public List<string> ServiceTemplates { get; set; } = [];

    /// <summary>
    /// Fixed name of the file inside the resolved folder that contains the
    /// process memory metrics lines (see MetricsLogParser). The folder is unique
    /// per service/PID, so this filename is constant across services.
    /// </summary>
    public string MetricsFileName { get; set; } = string.Empty;

    /// <summary>How often (seconds) to re-read the metrics file for new lines.</summary>
    public int MetricsIntervalSeconds { get; set; } = 90;

    /// <summary>
    /// UNC path template for the Log Browser's top-level "Root" node, per server.
    /// Placeholder: {server} — server hostname.
    /// Example: "\\{server}\Shared"
    /// </summary>
    public string RootFolder { get; set; } = string.Empty;

    /// <summary>
    /// Path the Log Browser auto-navigates to on startup, RELATIVE to <see cref="RootFolder"/>
    /// (not a full UNC template — RootFolder already supplies the {server}/UNC prefix).
    /// Supports the <c>{today}</c> placeholder, substituted with the current UTC date as
    /// yyyy-MM-dd. Example: "bau\logs\{today}".
    /// </summary>
    public string StartupFolder { get; set; } = string.Empty;

    /// <summary>Default filename glob for the Log Browser's file list/search (e.g. "*", "*.log").</summary>
    public string DefaultFileGlob { get; set; } = "*";

    /// <summary>
    /// List of server hostnames to enumerate in the Log Browser tree.
    /// If empty, the browser falls back to hosts seen in live heartbeats.
    /// </summary>
    public List<string> Servers { get; set; } = [];

    /// <summary>How often (seconds) the Log Browser polls the selected folder and its parent.</summary>
    public int FolderScanIntervalSeconds { get; set; } = 30;

    /// <summary>Minimum seconds between two polls of the same folder (throttle on click).</summary>
    public int FolderScanMinIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Minutes of inactivity (no subscribers) after which cached metrics data (file offsets + history)
    /// is released from memory. During the idle window polling is paused but data is preserved so
    /// re-subscribing resumes from where it left off without re-reading files from the start.
    /// </summary>
    public int IdleReleaseMinutes { get; set; } = 10;

    /// <summary>
    /// Caps how many matches a single recursive Log Browser search (Start Search) accumulates
    /// before it stops itself. Without a cap, searching a huge/misconfigured tree can grow the
    /// result list and the render workload without bound for as long as the search keeps running.
    /// </summary>
    public int MaxSearchResults { get; set; } = 5000;

    /// <summary>
    /// How the Log Browser reads a file's content when a file is opened. See
    /// <see cref="LogFileAccessMode"/>. Default: <see cref="LogFileAccessMode.ServerOnly"/>
    /// (unchanged historical behavior).
    /// </summary>
    public LogFileAccessMode FileAccessMode { get; set; } = LogFileAccessMode.ServerOnly;
}

/// <summary>
/// Controls how the Log Browser reads a file's content when opened (double-click, or the
/// toolbar "view" button). The client-side modes use the File System Access API — Chromium
/// only (Chrome/Edge; no Firefox/Safari) — reading directly from the log root's network
/// share instead of having the server read it and ship the content over the SignalR
/// circuit. The first file opened per server prompts a one-time "grant folder access"
/// dialog; later opens on that server reuse the granted handle (re-requesting permission
/// only, no dialog, if the browser reset the grant on reload).
/// </summary>
public enum LogFileAccessMode
{
    /// <summary>Server always reads the file and ships it over SignalR (original behavior).</summary>
    ServerOnly,

    /// <summary>
    /// Try client-side read first; if it fails for any reason (unsupported browser, user
    /// cancelled the grant, granted folder doesn't contain the file), fall back to
    /// <see cref="ServerOnly"/> automatically.
    /// </summary>
    ClientWithFallback,

    /// <summary>
    /// Client-side read only — if it fails, show an error instead of silently falling back
    /// to a server read. Useful for verifying the client-side path is actually working,
    /// since <see cref="ClientWithFallback"/>'s fallback can mask a broken client-side path.
    /// </summary>
    ClientOnly,
}
