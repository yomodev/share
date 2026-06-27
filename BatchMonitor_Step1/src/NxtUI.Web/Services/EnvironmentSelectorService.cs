using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Services;
using NxtUI.Models;

namespace NxtUI.Services;

/// <summary>
/// Scoped service that owns the "next tab environment" selection.
/// Both the sidebar dropdown and the Home environment selector write to this;
/// both read from it — bidirectional sync via <see cref="OnChange"/>.
/// </summary>
public class EnvironmentSelectorService
{
    private readonly IEnvironmentRegistry _registry;
    private string _selectedId;

    public EnvironmentSelectorService(IEnvironmentRegistry registry, IOptions<AppSettings> settings)
    {
        _registry   = registry;
        _selectedId = settings.Value.DefaultEnvironment;

        // Fall back to first environment if default is not in the list
        if (_registry.Find(_selectedId) is null && _registry.GetAll().Count > 0)
            _selectedId = _registry.GetAll()[0].Id;
    }

    /// <summary>All configured environments.</summary>
    public IReadOnlyList<EnvironmentInfo> Environments => _registry.GetAll();

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
    public EnvironmentInfo? Selected => _registry.Find(_selectedId);

    /// <summary>Raised when the selection changes, so all subscribers can re-render.</summary>
    public event Action? OnChange;
}
