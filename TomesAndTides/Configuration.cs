using Dalamud.Configuration;

namespace TomesAndTides;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool EnableAutoDutyLoopActions { get; set; }
    public uint MainSelectedTerritory { get; set; }
    public int MainSelectedDutyMode { get; set; }
    public int FarmingPeriodsToUse { get; set; } = 7;
    public bool MonitorRetainerVentures { get; set; }
    public int RetainerWaitBeforeDungeonMinutes { get; set; } = 5;
    public RetainerAutomationSessionRecord? RetainerSession { get; set; }
    public RetainerHandoffRecord? RetainerHandoff { get; set; }
    public DungeonRunRecord? DungeonRun { get; set; }
    public FarmingPlanRecord? FarmingPlan { get; set; }
    public List<FarmingPlanRecord> FarmingPlanHistory { get; set; } = [];
}

public sealed class RetainerAutomationSessionRecord
{
    public string SessionId { get; set; } = string.Empty;
    public string State { get; set; } = "Stopped";
    public string Detail { get; set; } = string.Empty;
    public ulong FarmingCharacterContentId { get; set; }
    public string FarmingCharacterName { get; set; } = string.Empty;
    public string FarmingCharacterWorld { get; set; } = string.Empty;
    public string FarmingPlanId { get; set; } = string.Empty;
    public bool ServiceRetainers { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? LastPassUtc { get; set; }
    public string? LastPassReadinessFingerprint { get; set; }
    public bool StopRequested { get; set; }
}

public sealed class RetainerHandoffRecord
{
    public string HandoffId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string? FarmingOperationId { get; set; }
    public ulong FarmingCharacterContentId { get; set; }
    public string FarmingCharacterName { get; set; } = string.Empty;
    public string FarmingCharacterWorld { get; set; } = string.Empty;
    public string State { get; set; } = "Preflight";
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset PhaseChangedUtc { get; set; }
    public DateTimeOffset? LaunchIntentUtc { get; set; }
    public DateTimeOffset? ReturnIntentUtc { get; set; }
    public bool CompletionEventObserved { get; set; }
    public bool SawProviderActive { get; set; }
    public string? ReadinessFingerprintAtLaunch { get; set; }
    public string? RecoveryKind { get; set; }
}

public sealed class FarmingPlanRecord
{
    public string PlanId { get; set; } = string.Empty;
    public string State { get; set; } = "PendingValidation";
    public string? RecoveryKind { get; set; }
    public string Detail { get; set; } = string.Empty;
    public ulong CharacterContentId { get; set; }
    public uint TerritoryId { get; set; }
    public uint ContentFinderConditionId { get; set; }
    public string DungeonName { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public int GearsetInspectionId { get; set; } = -1;
    public bool BareMode { get; set; }
    public bool ServiceRetainers { get; set; }
    public int RetainerWaitBeforeDungeonMinutes { get; set; } = 5;
    public string FarmingCharacterName { get; set; } = string.Empty;
    public string FarmingCharacterWorld { get; set; } = string.Empty;
    public int DungeonReward { get; set; }
    public int RequestedPeriodCount { get; set; } = 7;
    public uint CurrencyItemId { get; set; }
    public int WeeklyAcquiredAtStart { get; set; }
    public int WeeklyLimitAtStart { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset WeekStartUtc { get; set; }
    public DateTimeOffset WeeklyResetUtc { get; set; }
    public DateTimeOffset FinishDeadlineUtc { get; set; }
    public DateTimeOffset? CurrentPeriodStartUtc { get; set; }
    public bool StopRequested { get; set; }
    public string? Blocker { get; set; }
    public DateTimeOffset? RetryAfterUtc { get; set; }
    public string? CurrentOperationId { get; set; }
    public string? RetainerCheckpointOperationId { get; set; }
    public List<string> CompletedRetainerCheckpointOperationIds { get; set; } = [];
    public List<string> CreditedOperationIds { get; set; } = [];
    public List<FarmingPeriodRecord> Periods { get; set; } = [];
}

public sealed class FarmingPeriodRecord
{
    public DateTimeOffset StartUtc { get; set; }
    public int AllocatedRuns { get; set; }
    public int CompletedRuns { get; set; }
    public List<string> CreditedOperationIds { get; set; } = [];
}

public sealed class DungeonRunRecord
{
    public string OperationId { get; set; } = string.Empty;
    public string? FarmingPlanId { get; set; }
    public DateTimeOffset? FarmingPeriodStartUtc { get; set; }
    public uint TerritoryId { get; set; }
    public uint ContentFinderConditionId { get; set; }
    public string DungeonName { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public int GearsetInspectionId { get; set; } = -1;
    public ulong CharacterContentId { get; set; }
    public int ExpectedReward { get; set; }
    public int WeeklyAcquiredBefore { get; set; }
    public int WeeklyLimitAtRequest { get; set; }
    public uint CurrencyItemId { get; set; }
    public int? ObservedWeeklyDelta { get; set; }
    public string RewardOutcome { get; set; } = "Unknown";
    public DateTimeOffset RequestedUtc { get; set; }
    public DateTimeOffset? WeeklyResetUtcAtRequest { get; set; }
    public string State { get; set; } = "PendingValidation";
    public string Detail { get; set; } = string.Empty;
    public bool DutyStarted { get; set; }
    public bool DutyCompleted { get; set; }
    public int? WeeklyAcquiredAfter { get; set; }
    public bool? BareMode { get; set; }
}
