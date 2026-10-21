using TomesAndTides.Debugging;

namespace TomesAndTides.Integrations;

public interface IRetainerProvider
{
    event Action? SinglePassFinished;
    bool TryGetProviderState(out bool busy, out bool multiModeActive);
    RetainerReadinessSnapshot ReadReadiness(ulong currentCharacterContentId = 0);
    void RequestSingleRetainerPass();
}
