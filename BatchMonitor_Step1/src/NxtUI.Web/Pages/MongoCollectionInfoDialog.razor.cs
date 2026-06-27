namespace NxtUI.Pages;

public partial class MongoCollectionInfoDialog
{
    private static string FormatBytes(long b) => b switch
    {
        < 1024                => $"{b} B",
        < 1024 * 1024         => $"{b / 1024} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024 * 1024)} MB",
        _                     => $"{b / (1024L * 1024 * 1024)} GB",
    };
}
