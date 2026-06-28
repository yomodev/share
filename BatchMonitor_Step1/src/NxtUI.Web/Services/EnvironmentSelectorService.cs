using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Services;
using NxtUI.Core.Models;

namespace NxtUI.Web.Services;

/// <summary>
/// Scoped service that owns the "currently selected environment" for a Blazor circuit.
/// Both the sidebar dropdown and the Home environment selector write to this;
/// both read from it — bidirectional sync via <see cref="OnChange"/>.
/// </summary>
public class EnvironmentSelectorService
{
    private readonly IEnvironmentService _envService;
    private string _selectedId;

    public EnvironmentSelectorService(IEnvironmentService envService, IOptions<AppSettings> settings)
    {
        _envService = envService;
        _selectedId = settings.Value.DefaultEnvironment;

        if (_envService.GetById(_selectedId) is null && _envService.GetAll().Count > 0)
            _selectedId = _envService.GetAll()[0].Id;
    }

    public IReadOnlyList<EnvironmentInfo> Environments => _envService.GetAll();

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

    public EnvironmentInfo? Selected => _envService.GetById(_selectedId);

    public event Action? OnChange;
}
