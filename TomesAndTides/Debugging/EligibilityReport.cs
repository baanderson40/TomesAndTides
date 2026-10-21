namespace TomesAndTides.Debugging;

public enum EligibilityStatus
{
    Eligible,
    Blocked,
    Unknown,
}

public sealed record EligibilityCheck(
    string Name,
    EligibilityStatus Status,
    string Observed,
    string Required,
    string Explanation,
    string Source);

public sealed record EligibilityReport(IReadOnlyList<EligibilityCheck> Checks)
{
    public EligibilityStatus Overall => Checks.Any(check => check.Status == EligibilityStatus.Blocked)
        ? EligibilityStatus.Blocked
        : Checks.Any(check => check.Status == EligibilityStatus.Unknown)
            ? EligibilityStatus.Unknown
            : EligibilityStatus.Eligible;
}

public sealed record TomeRewardReadiness(
    EligibilityStatus WeeklyStatus,
    int? WeeklyRemaining,
    int? ExpectedPayout,
    EligibilityStatus StorageStatus,
    int? StorageRoom,
    string Explanation);

public static class TomeRewardPolicy
{
    public static TomeRewardReadiness Evaluate(
        int? dungeonReward,
        int? weeklyAcquired,
        int? weeklyLimit,
        int? heldBalance,
        int? holdingLimit)
    {
        var weeklyInputsValid = dungeonReward is > 0 && weeklyAcquired is >= 0 && weeklyLimit is > 0;
        var weeklyRemaining = weeklyInputsValid
            ? Math.Max(0, weeklyLimit!.Value - weeklyAcquired!.Value)
            : (int?)null;
        var expectedPayout = weeklyRemaining is { } remaining && dungeonReward is { } reward
            ? Math.Min(reward, remaining)
            : (int?)null;
        var weeklyStatus = weeklyRemaining is null ? EligibilityStatus.Unknown :
            weeklyRemaining > 0 ? EligibilityStatus.Eligible : EligibilityStatus.Blocked;
        var storageInputsValid = expectedPayout is not null && heldBalance is >= 0 && holdingLimit is > 0;
        var storageRoom = storageInputsValid
            ? Math.Max(0, holdingLimit!.Value - heldBalance!.Value)
            : (int?)null;
        var storageStatus = storageRoom is null ? EligibilityStatus.Unknown :
            storageRoom >= expectedPayout ? EligibilityStatus.Eligible : EligibilityStatus.Blocked;
        var explanation = weeklyRemaining == 0
            ? "Weekly cap reached; no further capped-tome payout is available."
            : storageRoom is null
                ? "Expected payout or held-currency capacity inputs are unavailable or invalid."
                : storageRoom < expectedPayout
                    ? $"Only {storageRoom} storage space remains, below the expected payout of {expectedPayout}. Spend tomes before farming."
                    : $"Expected payout {expectedPayout}; {storageRoom} storage space remains.";
        return new(weeklyStatus, weeklyRemaining, expectedPayout, storageStatus, storageRoom, explanation);
    }
}
