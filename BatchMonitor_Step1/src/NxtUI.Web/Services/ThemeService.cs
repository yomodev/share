using Microsoft.AspNetCore.Http;
using MudBlazor;

namespace NxtUI.Services;

public class ThemeService
{
    private bool _isDark;

    public ThemeService(IHttpContextAccessor http)
    {
        var cookie = http.HttpContext?.Request.Cookies["bm-theme"];
        _isDark = cookie != "light"; // default: dark
    }

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

    public event Action? OnChange;

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Background               = "#F6F8FA",
            BackgroundGray           = "#EAEEF2",
            Surface                  = "#FFFFFF",
            DrawerBackground         = "#F6F8FA",
            AppbarBackground         = "#FFFFFF",
            Divider                  = "#D0D7DE",
            DividerLight             = "#EAEEF2",
            TableLines               = "#D0D7DE",
            TextPrimary              = "#1F2328",
            TextSecondary            = "#57606A",
            TextDisabled             = "#818B98",
            Primary                  = "#0969DA",
            PrimaryContrastText      = "#ffffff",
            Secondary                = "#57606A",
            Info                     = "#0550AE",
            Success                  = "#1A7F37",
            Warning                  = "#9A6700",
            Error                    = "#CF222E",
            ActionDefault            = "#57606A",
            ActionDisabled           = "#818B98",
            ActionDisabledBackground = "#EAEEF2",
            DrawerText               = "#1F2328",
            DrawerIcon               = "#57606A",
            AppbarText               = "#1F2328",
            OverlayDark              = "rgba(0,0,0,0.4)",
        },
        PaletteDark = new PaletteDark
        {
            Background               = "#0E1117",
            BackgroundGray           = "#161B22",
            Surface                  = "#161B22",
            DrawerBackground         = "#0E1117",
            AppbarBackground         = "#0E1117",
            Divider                  = "#30363D",
            DividerLight             = "#21262D",
            TableLines               = "#30363D",
            TextPrimary              = "#E6EDF3",
            TextSecondary            = "#8B949E",
            TextDisabled             = "#484F58",
            Primary                  = "#2F81F4",
            PrimaryContrastText      = "#ffffff",
            Secondary                = "#8B949E",
            Info                     = "#388BFD",
            Success                  = "#3FB950",
            Warning                  = "#D29922",
            Error                    = "#F85149",
            ActionDefault            = "#8B949E",
            ActionDisabled           = "#484F58",
            ActionDisabledBackground = "#21262D",
            DrawerText               = "#E6EDF3",
            DrawerIcon               = "#8B949E",
            AppbarText               = "#E6EDF3",
            OverlayDark              = "rgba(0,0,0,0.6)",
        },
        LayoutProperties = new LayoutProperties
        {
            DrawerWidthLeft     = "240px",
            DrawerMiniWidthLeft = "64px",
            AppbarHeight        = "44px",
        },
    };
}
