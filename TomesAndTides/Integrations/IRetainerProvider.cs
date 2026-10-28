using TomesAndTides.Debugging;

namespace TomesAndTides.Integrations;

public interface IRetainerProvider
{
    event Action? SinglePassFinished;
    bool TryGetProviderState(out bool busy, out bool multiModeActive);
    RetainerReadinessSnapshot ReadReadiness(ulong currentCharacterContentId = 0);
    bool TryGetRegisteredCharacters(out IReadOnlyList<RetainerCharacterIdentity> characters);
    void RequestSingleRetainerPass();
    bool TryCancelOwnedPass(out string detail);
}

public sealed record RetainerCharacterIdentity(ulong ContentId, string Name, string World);
