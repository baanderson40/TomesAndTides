using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using TomesAndTides.Debugging;

namespace TomesAndTides.Integrations;

/// <summary>Framework-thread adapter for game state consumed by automation controllers.</summary>
public sealed class DalamudWorkflowGameState : IWorkflowGameState, IDisposable
{
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly IPlayerState playerState;
    private readonly ICondition conditions;
    private readonly IDutyState dutyState;

    public event Action? DutyStarted;
    public event Action? DutyCompleted;

    public DalamudWorkflowGameState(IClientState client, IObjectTable objects, IPlayerState playerState,
        ICondition conditions, IDutyState dutyState)
    {
        this.client = client;
        this.objects = objects;
        this.playerState = playerState;
        this.conditions = conditions;
        this.dutyState = dutyState;
        dutyState.DutyStarted += OnDutyStarted;
        dutyState.DutyCompleted += OnDutyCompleted;
    }

    public ulong ContentId => playerState.ContentId;
    public bool IsLoggedIn => client.IsLoggedIn;
    public bool HasLocalPlayer => objects.LocalPlayer is not null;
    public bool IsLocalPlayerAlive => objects.LocalPlayer is { CurrentHp: > 0 };
    public uint TerritoryType => client.TerritoryType;
    public uint ContentFinderConditionId => dutyState.ContentFinderCondition.RowId;
    public bool IsDutyStarted => dutyState.IsDutyStarted;
    public bool IsBoundByDuty => conditions[ConditionFlag.BoundByDuty] ||
                                 conditions[ConditionFlag.BoundByDuty56] ||
                                 conditions[ConditionFlag.BoundByDuty95];
    public bool InCombat => conditions[ConditionFlag.InCombat];
    public bool WaitingForDutyFinder => conditions[ConditionFlag.WaitingForDutyFinder];
    public bool BetweenAreas => conditions[ConditionFlag.BetweenAreas];
    public bool BetweenAreas51 => conditions[ConditionFlag.BetweenAreas51];
    public bool Occupied => conditions[ConditionFlag.Occupied];
    public bool IsSafePlayerState => IsLoggedIn && IsLocalPlayerAlive &&
        !IsDutyStarted && !InCombat && !WaitingForDutyFinder && !BetweenAreas && !BetweenAreas51;
    public string CharacterName => objects.LocalPlayer?.Name.ToString() ?? string.Empty;
    public string HomeWorld => objects.LocalPlayer?.HomeWorld.Value.Name.ToString() ?? string.Empty;

    public void Dispose()
    {
        dutyState.DutyStarted -= OnDutyStarted;
        dutyState.DutyCompleted -= OnDutyCompleted;
    }

    private void OnDutyStarted(IDutyStateEventArgs _) => DutyStarted?.Invoke();
    private void OnDutyCompleted(IDutyStateEventArgs _) => DutyCompleted?.Invoke();
}
