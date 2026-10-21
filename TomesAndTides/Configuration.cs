using Dalamud.Configuration;

namespace TomesAndTides;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool EnableAutoDutyLoopActions { get; set; }
    public uint MainSelectedTerritory { get; set; }
    public int MainSelectedDutyMode { get; set; }
    public DungeonRunRecord? DungeonRun { get; set; }
}

public sealed class DungeonRunRecord
{
    public string OperationId { get; set; } = string.Empty;
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
