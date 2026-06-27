namespace NxtUI.Components;

public partial class KafkaTopicInfoDialog
{
    private static string FormatRetention(long ms) => ms switch
    {
        < 0                => "∞",
        0                  => "0",
        < 3_600_000        => $"{ms / 60_000}m",
        < 86_400_000       => $"{ms / 3_600_000}h",
        < 7L * 86_400_000  => $"{ms / 86_400_000}d",
        < 30L * 86_400_000 => $"{ms / (7L * 86_400_000)}w",
        _                  => $"{ms / (30L * 86_400_000)}mo",
    };

    private static string FormatBytes(long b) => b switch
    {
        < 1024                => $"{b} B",
        < 1024 * 1024         => $"{b / 1024} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024 * 1024)} MB",
        _                     => $"{b / (1024L * 1024 * 1024)} GB",
    };
}
