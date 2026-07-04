namespace NxtUI.Web.Pages;

public partial class ServicesPage
{
    // Same blue → green → yellow → orange → red → deeppink gradient as the Home
    // memory treemap (d3-home-treemap.js ramColor()) — kept in sync by hand,
    // same tradeoff noted in docs/10_CodeReviewNotes.md for the log-format parsers.
    private static readonly (double Mb, byte R, byte G, byte B)[] MemGradientStops =
    [
        (0,    59, 130, 246), // blue      #3B82F6
        (100,  34, 197, 94),  // green     #22C55E
        (250,  234, 179, 8),  // yellow    #EAB308
        (450,  249, 115, 22), // orange    #F97316
        (700,  239, 68, 68),  // red       #EF4444
        (1100, 255, 20, 147), // deeppink  #FF1493
    ];

    private static string? MemGradientColor(double? mb)
    {
        if (mb is null || mb <= 0) return null;
        var stops = MemGradientStops;
        var v = mb.Value;

        if (v <= stops[0].Mb) return Rgb(stops[0]);
        var last = stops[^1];
        if (v >= last.Mb) return Rgb(last);

        for (var i = 0; i < stops.Length - 1; i++)
        {
            var s0 = stops[i];
            var s1 = stops[i + 1];
            if (v < s0.Mb || v > s1.Mb) continue;

            var t = (v - s0.Mb) / (s1.Mb - s0.Mb);
            var r = (byte)Math.Round(s0.R + (s1.R - s0.R) * t);
            var g = (byte)Math.Round(s0.G + (s1.G - s0.G) * t);
            var b = (byte)Math.Round(s0.B + (s1.B - s0.B) * t);
            return $"rgb({r},{g},{b})";
        }
        return Rgb(last);
    }

    private static string Rgb((double Mb, byte R, byte G, byte B) s) => $"rgb({s.R},{s.G},{s.B})";

    /// <summary>Inline color style for a Mem value — empty string (default text color) when there's no sample yet.</summary>
    private static string MemColorStyle(double? mb)
    {
        var color = MemGradientColor(mb);
        return color is null ? "" : $"color:{color};";
    }
}
