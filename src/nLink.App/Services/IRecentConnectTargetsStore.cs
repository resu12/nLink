using System.Collections.Generic;

namespace NLink.App.Services;

public interface IRecentConnectTargetsStore
{
    IReadOnlyList<string> LoadTargets();

    void SaveTargets(IReadOnlyList<string> targets);
}
