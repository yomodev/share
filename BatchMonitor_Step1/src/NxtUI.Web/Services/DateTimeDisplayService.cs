using Microsoft.AspNetCore.Http;

namespace NxtUI.Web.Services;

/// <summary>
/// Tracks the user's date/time display preference (UTC vs local) and exposes
/// a helper to convert UTC datetimes for rendering.
/// Preference is persisted in the "bm-tz" cookie so it survives page reloads.
/// </summary>
public class DateTimeDisplayService
{
    private bool _useUtc;

    private bool _devMode;

    public DateTimeDisplayService(IHttpContextAccessor http)
    {
        var cookies = http.HttpContext?.Request.Cookies;
        _useUtc  = cookies?["bm-tz"]  == "utc";
        _devMode = cookies?["bm-dev"] == "1";
    }

    public bool DevMode
    {
        get => _devMode;
        set
        {
            if (_devMode == value) return;
            _devMode = value;
            OnChange?.Invoke();
        }
    }

    public bool UseUtc
    {
        get => _useUtc;
        set
        {
            if (_useUtc == value) return;
            _useUtc = value;
            OnChange?.Invoke();
        }
    }

    /// <summary>Convert a UTC datetime for display (to local if setting is local).</summary>
    public DateTime ToDisplay(DateTime dt)
    {
        if (_useUtc) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
    }

    /// <summary>Format a UTC datetime for display using the current preference.</summary>
    public string Format(DateTime dt, string format) => ToDisplay(dt).ToString(format);

    public string ZoneSuffix => _useUtc ? " UTC" : "";

    public event Action? OnChange;
}
