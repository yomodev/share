using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

public interface IEnvironmentService
{
    IReadOnlyList<EnvironmentInfo> GetAll();
    EnvironmentInfo?               GetById(string id);
    IReadOnlyList<string>          GetServers(string environmentId);
}
