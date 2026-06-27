namespace NxtUI.Pages;

public partial class ServicesPage
{
    private static string RamColorClass(double? mb) => mb switch
    {
        null or <= 0 => "",
        < 512        => "bm-ram-green",
        < 1024       => "bm-ram-yellow",
        < 4096       => "bm-ram-orange",
        _            => "bm-ram-red",
    };
}
