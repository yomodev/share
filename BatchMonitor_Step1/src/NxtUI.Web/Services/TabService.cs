using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Scoped service that owns the list of open tabs for a Blazor circuit.
/// All mutations raise <see cref="OnChange"/> so the tab bar re-renders.
/// </summary>
public class TabService
{
    private readonly List<TabModel> _tabs = new();

    /// <summary>Currently focused tab, or null when Home is shown.</summary>
    public TabModel? ActiveTab { get; private set; }

    /// <summary>Read-only snapshot of open tabs in insertion order.</summary>
    public IReadOnlyList<TabModel> Tabs => _tabs;

    /// <summary>Raised whenever the tab list or active tab changes.</summary>
    public event Action? OnChange;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Opens the tab if not already open, then focuses it.
    /// If the tab is already open, just focuses it (no duplicate).
    /// </summary>
    public void OpenOrFocus(TabModel tab)
    {
        var existing = _tabs.FirstOrDefault(t => t.Id == tab.Id);
        if (existing is null)
        {
            _tabs.Add(tab);
            existing = tab;
        }
        SetActive(existing);
    }

    /// <summary>
    /// Focuses an already-open tab by id. No-op if not found.
    /// </summary>
    public void Focus(string tabId)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is not null && !tab.IsActive)
            SetActive(tab);
    }

    /// <summary>
    /// Closes a tab. Focus moves to the nearest remaining tab (left preferred),
    /// or to Home if no tabs remain.
    /// </summary>
    public void Close(string tabId)
    {
        var idx = _tabs.FindIndex(t => t.Id == tabId);
        if (idx < 0) return;

        var wasActive = _tabs[idx].IsActive;
        _tabs.RemoveAt(idx);

        if (wasActive)
        {
            if (_tabs.Count == 0)
            {
                ActiveTab = null;
            }
            else
            {
                // prefer left, fall back to right
                var newIdx = Math.Max(0, idx - 1);
                SetActive(_tabs[newIdx]);
                return; // SetActive already notifies
            }
        }

        Notify();
    }

    /// <summary>
    /// Unfocuses all tabs and returns to Home.
    /// </summary>
    public void GoHome()
    {
        foreach (var t in _tabs) t.IsActive = false;
        ActiveTab = null;
        Notify();
    }

    /// <summary>Returns true if a tab with the given id is currently open.</summary>
    public bool IsOpen(string tabId) => _tabs.Any(t => t.Id == tabId);

    /// <summary>Returns true if the tab with the given id is the active tab.</summary>
    public bool IsActive(string tabId) => ActiveTab?.Id == tabId;

    /// <summary>Renames the tab's display label and optionally its icon. No-op if not found or label is blank.</summary>
    public void RenameTab(string tabId, string newLabel, string? newIcon = null)
    {
        newLabel = newLabel.Trim();
        if (string.IsNullOrEmpty(newLabel)) return;
        var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;
        tab.Label = newLabel;
        if (newIcon is not null) tab.Icon = newIcon;
        Notify();
    }

    /// <summary>
    /// Moves the tab with id <paramref name="tabId"/> so it sits immediately
    /// before the tab currently at <paramref name="targetIndex"/> (0-based,
    /// measured in the list *after* the moved tab is removed). No-op if the
    /// tab isn't found or the resulting position is unchanged.
    /// </summary>
    public void Reorder(string tabId, int targetIndex)
    {
        var idx = _tabs.FindIndex(t => t.Id == tabId);
        if (idx < 0) return;

        var tab = _tabs[idx];
        _tabs.RemoveAt(idx);

        targetIndex = Math.Clamp(targetIndex, 0, _tabs.Count);
        _tabs.Insert(targetIndex, tab);

        Notify();
    }

    // ── Internals ────────────────────────────────────────────────────────

    private void SetActive(TabModel tab)
    {
        foreach (var t in _tabs) t.IsActive = false;
        tab.IsActive = true;
        ActiveTab = tab;
        Notify();
    }

    private void Notify() => OnChange?.Invoke();
}
