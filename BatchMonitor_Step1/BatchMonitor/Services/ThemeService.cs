using MudBlazor;

namespace BatchMonitor.Services;

/// <summary>
/// Scoped service that owns the dark/light theme preference.
/// Settings page writes to this; MainLayout reads from it.
/// Persistence to localStorage is handled by the Settings page via JS interop.
/// </summary>
public class ThemeService
{
    private bool _isDark = true;

    /// <summary>True when dark theme is active.</summary>
    public bool IsDark
    {
        get => _isDark;
        set
        {
            if (_isDark == value) return;
            _isDark = value;
            OnChange?.Invoke();
        }
    }

    /// <summary>Raised when the theme changes so MainLayout can re-render.</summary>
    public event Action? OnChange;

    /// <summary>Returns the active MudBlazor theme.</summary>
    public MudTheme ActiveTheme => _isDark ? DarkTheme : LightTheme;

    // ── Theme definitions ─────────────────────────────────────────────────

    public static readonly MudTheme DarkTheme = new()
    {
        PaletteLight = new PaletteLight(),   // unused in dark mode
        PaletteDark = new PaletteDark
        {
            // Backgrounds
            Background          = "#0E1117",
            BackgroundGray      = "#161B22",
            Surface             = "#161B22",
            DrawerBackground    = "#0E1117",
            AppbarBackground    = "#0E1117",

            // Borders & dividers
            Divider             = "#30363D",
            DividerLight        = "#21262D",
            TableLines          = "#30363D",

            // Text
            TextPrimary         = "#E6EDF3",
            TextSecondary       = "#8B949E",
            TextDisabled        = "#484F58",

            // Accents
            Primary             = "#2F81F4",
            PrimaryContrastText = "#ffffff",
            Secondary           = "#8B949E",
            Info                = "#388BFD",
            Success             = "#3FB950",
            Warning             = "#D29922",
            Error               = "#F85149",

            // Overlays
            OverlayDark         = "rgba(0,0,0,0.6)",
            OverlayLight        = "rgba(14,17,23,0.4)",

            ActionDefault       = "#8B949E",
            ActionDisabled      = "#484F58",
            ActionDisabledBackground = "#21262D",

            DrawerText          = "#E6EDF3",
            DrawerIcon          = "#8B949E",
            AppbarText          = "#E6EDF3",
        },
        LayoutProperties = new LayoutProperties
        {
            DrawerWidthLeft     = "240px",
            DrawerMiniWidthLeft = "64px",
            AppbarHeight        = "48px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = new[] { "Inter", "Segoe UI", "system-ui", "sans-serif" },
                FontSize   = "0.875rem",
                LineHeight = "1.5"
            },
            H6 = new H6Typography
            {
                FontFamily = new[] { "Inter", "Segoe UI", "system-ui", "sans-serif" },
                FontSize   = "1rem",
                FontWeight = "600"
            }
        },
        ZIndex = new ZIndex { Drawer = 1200, AppBar = 1100 }
    };

    public static readonly MudTheme LightTheme = new()
    {
        PaletteLight = new PaletteLight
        {
            // Backgrounds
            Background          = "#F6F8FA",
            BackgroundGray      = "#EAEEF2",
            Surface             = "#FFFFFF",
            DrawerBackground    = "#F6F8FA",
            AppbarBackground    = "#FFFFFF",

            // Borders & dividers
            Divider             = "#D0D7DE",
            DividerLight        = "#EAEEF2",
            TableLines          = "#D0D7DE",

            // Text
            TextPrimary         = "#1F2328",
            TextSecondary       = "#57606A",
            TextDisabled        = "#818B98",

            // Accents
            Primary             = "#0969DA",
            PrimaryContrastText = "#ffffff",
            Secondary           = "#57606A",
            Info                = "#0550AE",
            Success             = "#1A7F37",
            Warning             = "#9A6700",
            Error               = "#CF222E",

            // Overlays
            OverlayDark         = "rgba(0,0,0,0.4)",
            OverlayLight        = "rgba(246,248,250,0.8)",

            ActionDefault       = "#57606A",
            ActionDisabled      = "#818B98",
            ActionDisabledBackground = "#EAEEF2",

            DrawerText          = "#1F2328",
            DrawerIcon          = "#57606A",
            AppbarText          = "#1F2328",
        },
        LayoutProperties = new LayoutProperties
        {
            DrawerWidthLeft     = "240px",
            DrawerMiniWidthLeft = "64px",
            AppbarHeight        = "48px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = new[] { "Inter", "Segoe UI", "system-ui", "sans-serif" },
                FontSize   = "0.875rem",
                LineHeight = "1.5"
            },
            H6 = new H6Typography
            {
                FontFamily = new[] { "Inter", "Segoe UI", "system-ui", "sans-serif" },
                FontSize   = "1rem",
                FontWeight = "600"
            }
        },
        ZIndex = new ZIndex { Drawer = 1200, AppBar = 1100 }
    };
}
