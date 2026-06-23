namespace NxtUI.Models;

public class ServiceStatus
{
    public string ServiceName { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTime UpdatedDateTime { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public bool IsOnline { get; set; }

    // Latest memory metrics in MB (current+child / peak+child peak), stamped from
    // the metrics monitor for display, sorting and filtering. Null until a sample
    // is available. Not part of the heartbeat document.
    public double? RamMb  { get; set; }
    public double? PeakMb { get; set; }
}
