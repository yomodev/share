using NxtUI.Web.Models;

namespace NxtUI.Web.Services;

/// <summary>
/// Collapses the repeated
/// <c>Tab?.BeginLoading(); TabService.NotifyChanged(); ... finally { Tab?.EndLoading(); TabService.NotifyChanged(); }</c>
/// try/finally block duplicated across every dashboard page's load method into a
/// single <c>using</c>. Construction begins loading, disposal ends it.
/// </summary>
public readonly struct TabLoadingScope : IDisposable
{
    private readonly TabModel? _tab;
    private readonly TabService _tabService;

    private TabLoadingScope(TabModel? tab, TabService tabService)
    {
        _tab = tab;
        _tabService = tabService;
        _tab?.BeginLoading();
        _tabService.NotifyChanged();
    }

    public static TabLoadingScope Begin(TabModel? tab, TabService tabService) => new(tab, tabService);

    public void Dispose()
    {
        _tab?.EndLoading();
        _tabService.NotifyChanged();
    }
}
