namespace TomesAndTides.Debugging;

/// <summary>Small game-state surface used by the farming and retainer controllers.</summary>
public interface IWorkflowGameState
{
    ulong ContentId { get; }
    bool IsLoggedIn { get; }
    bool HasLocalPlayer { get; }
    bool IsLocalPlayerAlive { get; }
    uint TerritoryType { get; }
    uint ContentFinderConditionId { get; }
    bool IsDutyStarted { get; }
    bool IsBoundByDuty { get; }
    bool InCombat { get; }
    bool WaitingForDutyFinder { get; }
    bool BetweenAreas { get; }
    bool BetweenAreas51 { get; }
    bool Occupied { get; }
    bool IsSafePlayerState { get; }
    string CharacterName { get; }
    string HomeWorld { get; }
    event Action? DutyStarted;
    event Action? DutyCompleted;
}
