namespace TomesAndTides.Debugging;

/// <summary>Pure policy helpers for the persisted, at-most-once run lifecycle.</summary>
public static class SingleRunPolicy
{
    public enum RewardObservation { Wait, Verified, Unverified, ResetCrossed, IdentityMismatch }

    public static bool MayDispatch(string state) => state == "PendingValidation";

    public static string StateAfterReload(string state) => state is "Completed" or "Failed" or "RecoveryRequired"
        ? state
        : "RecoveryRequired";

    public static RewardObservation ClassifyReward(int expected, int? delta, bool timedOut,
        bool resetCrossed, bool identityMatches)
    {
        if (resetCrossed) return RewardObservation.ResetCrossed;
        if (!identityMatches) return RewardObservation.IdentityMismatch;
        if (delta == expected) return RewardObservation.Verified;
        if (delta is < 0 || delta > expected) return RewardObservation.Unverified;
        return timedOut ? RewardObservation.Unverified : RewardObservation.Wait;
    }

    public static bool TimedOut(DateTimeOffset now, DateTimeOffset started, TimeSpan timeout) =>
        now - started > timeout;

    public static bool BareModeFor(bool enableLoopActions) => !enableLoopActions;
}
