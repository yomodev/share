using NxtUI.Models;

namespace NxtUI.Logging;

/// <summary>
/// Substitutes the log-path template placeholders for a given service + environment.
/// Shared by LogPathDiscoveryService (to resolve) and the test log generator (to
/// produce matching folders). The result may still contain '*' wildcards.
/// </summary>
public static class LogPathTemplate
{
    public static string Expand(string template, ServiceStatus svc, string env, DateTime? date = null)
    {
        var d = date ?? svc.CreatedDateTime;
        return template
            // {date-1} before {date} so the longer token wins.
            .Replace("{date-1}", d.AddDays(-1).ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}",   d.ToString("yyyy-MM-dd"),             StringComparison.OrdinalIgnoreCase)
            .Replace("{server}", svc.HostName,                         StringComparison.OrdinalIgnoreCase)
            .Replace("{service}", svc.ServiceName,                     StringComparison.OrdinalIgnoreCase)
            .Replace("{pid}",    svc.ProcessId.ToString(),             StringComparison.OrdinalIgnoreCase)
            .Replace("{env}",    env,                                  StringComparison.OrdinalIgnoreCase);
    }
}
