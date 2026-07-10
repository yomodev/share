namespace NxtUI.Core.Configuration;

/// <summary>
/// Settings for locating and browsing per-server log/service folders (Log Browser page,
/// plus service-folder resolution used by run-detail/services log links). Bound from
/// appsettings.json "FileBrowser" section.
/// </summary>
public class FileBrowserSettings
{
    public const string SectionName = "FileBrowser";

    /// <summary>
    /// Ordered list of UNC path templates for locating a service's log/metrics folder. Placeholders:
    ///   {server}  — HostName from heartbeat
    ///   {service} — ServiceName from heartbeat
    ///   {pid}     — ProcessId from heartbeat
    ///   {env}     — environment Id (e.g. UAT1)
    ///   {date}    — CreatedDateTime as yyyy-MM-dd
    ///   {date-1}  — CreatedDateTime minus one day as yyyy-MM-dd
    /// A segment may contain * which is expanded via Directory.GetDirectories.
    /// First template whose path resolves wins. Example:
    ///   "\\{server}\Shared\bau\logs\{date}\Services\{service}_{env}\{server}_*_Pid_{pid}"
    /// </summary>
    public List<string> ServiceTemplates { get; set; } = [];

    /// <summary>
    /// UNC path template for the Log Browser's top-level "Root" node, per server.
    /// Placeholder: {server} — server hostname. Example: "\\{server}\Shared"
    /// </summary>
    public string RootFolder { get; set; } = string.Empty;

    /// <summary>
    /// Path the Log Browser auto-navigates to on startup, RELATIVE to <see cref="RootFolder"/>
    /// (not a full UNC template — RootFolder already supplies the {server}/UNC prefix).
    /// Supports the <c>{today}</c> placeholder, substituted with the current UTC date as
    /// yyyy-MM-dd. Example: "bau\logs\{today}".
    /// </summary>
    public string StartupFolder { get; set; } = string.Empty;

    /// <summary>Default filename glob for the Log Browser's file list/search. Example: "*", "*.log".</summary>
    public string DefaultFileGlob { get; set; } = "*";

    /// <summary>
    /// List of server hostnames to enumerate in the Log Browser tree.
    /// If empty, the browser falls back to hosts seen in live heartbeats.
    /// </summary>
    public List<string> Servers { get; set; } = [];

    /// <summary>How often (seconds) the Log Browser polls the selected folder and its parent. Default: 30.</summary>
    public int FolderScanIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Caps how many matches a single recursive Log Browser search (Start Search) accumulates
    /// before it stops itself. Without a cap, searching a huge/misconfigured tree can grow the
    /// result list and the render workload without bound for as long as the search keeps running.
    /// Default: 5000.
    /// </summary>
    public int MaxSearchResults { get; set; } = 5000;

    /// <summary>
    /// How the Log Browser reads a file's content when a file is opened. See
    /// <see cref="LogFileAccessMode"/>. Default: <see cref="LogFileAccessMode.ServerOnly"/>.
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
