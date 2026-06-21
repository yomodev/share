namespace BatchMonitor.Models;

public class ServiceStatus
{
    public string ServiceName { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTime UpdatedDateTime { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public bool IsOnline { get; set; }
}
