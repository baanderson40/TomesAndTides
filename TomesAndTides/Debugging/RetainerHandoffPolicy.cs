namespace TomesAndTides.Debugging;

/// <summary>Pure admission policy for beginning an additional AutoRetainer pass.</summary>
public static class RetainerHandoffPolicy
{
    public static bool ReturnVerificationTimedOut(DateTimeOffset now, DateTimeOffset phaseChangedUtc,
        TimeSpan timeout) => now - phaseChangedUtc >= timeout;

    public static bool HandoffMatchesCheckpoint(string? handoffSessionId, string expectedSessionId,
        string? handoffOperationId, string? checkpointOperationId) =>
        string.Equals(handoffSessionId, expectedSessionId, StringComparison.Ordinal) &&
        (checkpointOperationId is null || string.Equals(handoffOperationId, checkpointOperationId, StringComparison.Ordinal));

    public static bool MayReconcileCompletedCheckpoint(string handoffState, bool completionEventObserved,
        string? handoffSessionId, string expectedSessionId, string? handoffOperationId, string? checkpointOperationId) =>
        handoffState == "Completed" && completionEventObserved && checkpointOperationId is not null &&
        HandoffMatchesCheckpoint(handoffSessionId, expectedSessionId, handoffOperationId, checkpointOperationId);

    public static bool ShouldWaitForNearCompleteVenture(bool readinessKnown, bool ready, long? closestSeconds,
        int waitMinutes) => readinessKnown && !ready && waitMinutes is >= 0 and <= 30 &&
                            closestSeconds is { } seconds && seconds <= waitMinutes * 60L;

    public static bool IsUnchangedReadyWork(string? previousFingerprint, string currentFingerprint) =>
        !string.IsNullOrWhiteSpace(previousFingerprint) && !string.IsNullOrWhiteSpace(currentFingerprint) &&
        string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal);

    public static bool MayRequestPass(
        bool sessionActive,
        bool stopRequested,
        bool monitoringEnabled,
        bool beforeWeeklyReset,
        bool dungeonPrioritySatisfied,
        bool dungeonOperationPresent,
        bool handoffOwned,
        bool readinessKnown,
        bool readinessReady,
        bool readinessFresh,
        bool cooldownElapsed) =>
        sessionActive && !stopRequested && monitoringEnabled && beforeWeeklyReset &&
        dungeonPrioritySatisfied && !dungeonOperationPresent && !handoffOwned &&
        readinessKnown && readinessReady && readinessFresh && cooldownElapsed;

    public static bool MayReleaseOwnership(
        bool completionEventObserved,
        bool providerIdle,
        bool stableIdle,
        bool farmingCharacterMatched,
        bool playerSafe,
        bool autoDutyStopped,
        bool lifestreamIdle) =>
        completionEventObserved && providerIdle && stableIdle && farmingCharacterMatched &&
        playerSafe && autoDutyStopped && lifestreamIdle;
}
