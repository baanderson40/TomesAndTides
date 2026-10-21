namespace TomesAndTides.Debugging;

public sealed record DashboardSnapshot(
    DateTimeOffset CapturedUtc,
    bool LoggedIn,
    ulong? CharacterContentId,
    string CharacterName,
    string CurrencyName,
    int? WeeklyAcquired,
    int? WeeklyLimit,
    ulong? HeldBalance,
    bool? AutoDutyStopped,
    uint SelectedTerritory,
    DebugTests.DutyModeSelection SelectedMode,
    DebugReport? Eligibility,
    string? Error = null,
    string CharacterHomeWorld = "");
