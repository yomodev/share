using BatchMonitor.Configuration;
using BatchMonitor.Models;
using Microsoft.Extensions.Options;

namespace BatchMonitor.Services;

/// <summary>
/// Scoped service that owns the "next tab environment" selection.
/// Both the sidebar dropdown and the Home environment selector write to this;
/// both read from it — bidirectional sync via <see cref="OnChange"/>.
/// </summary>
public class EnvironmentSelectorService
{
    private readonly List<EnvironmentInfo> _environments;
    private string _selectedId;

    public EnvironmentSelectorService(IOptions<AppSettings> settings)
    {
        _environments = settings.Value.Environments
            .Select(e => new EnvironmentInfo
            {
                Id    = e.Id,
                Label = e.Label,
                Tier  = e.Tier
            })
            .ToList();

        _selectedId = settings.Value.DefaultEnvironment;

        // Fall back to first environment if default is not in the list
        if (_environments.All(e => e.Id != _selectedId) && _environments.Count > 0)
            _selectedId = _environments[0].Id;
    }

    /// <summary>All configured environments.</summary>
    public IReadOnlyList<EnvironmentInfo> Environments => _environments;

    /// <summary>Currently selected environment id.</summary>
    public string SelectedId
    {
        get => _selectedId;
        set
        {
            if (_selectedId == value) return;
            _selectedId = value;
            OnChange?.Invoke();
        }
    }

    /// <summary>The full <see cref="EnvironmentInfo"/> for the selected environment.</summary>
    public EnvironmentInfo? Selected =>
        _environments.FirstOrDefault(e => e.Id == _selectedId);

    /// <summary>Raised when the selection changes, so all subscribers can re-render.</summary>
    public event Action? OnChange;
}
