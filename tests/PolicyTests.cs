using TomesAndTides.Debugging;

var tests = new (string Name, Action Run)[]
{
    ("all eligible", () => AssertOverall(EligibilityStatus.Eligible,
        Check(EligibilityStatus.Eligible), Check(EligibilityStatus.Eligible))),
    ("unknown blocks approval", () => AssertOverall(EligibilityStatus.Unknown,
        Check(EligibilityStatus.Eligible), Check(EligibilityStatus.Unknown))),
    ("known blocker dominates unknown", () => AssertOverall(EligibilityStatus.Blocked,
        Check(EligibilityStatus.Unknown), Check(EligibilityStatus.Blocked))),
    ("storage room is sufficient", () => AssertTome(EligibilityStatus.Eligible,
        EligibilityStatus.Eligible, 915, 20, 0, 900, 1085, 2000)),
    ("storage room exactly fits payout", () => AssertTome(EligibilityStatus.Eligible,
        EligibilityStatus.Eligible, 20, 20, 0, 900, 1980, 2000)),
    ("insufficient storage blocks", () => AssertTome(EligibilityStatus.Eligible,
        EligibilityStatus.Blocked, 5, 20, 0, 900, 1995, 2000)),
    ("weekly cap clamps storage requirement", () => AssertTome(EligibilityStatus.Eligible,
        EligibilityStatus.Eligible, 15, 15, 885, 900, 1985, 2000)),
    ("weekly cap reached blocks farming", () => AssertTome(EligibilityStatus.Blocked,
        EligibilityStatus.Eligible, 100, 0, 900, 900, 1900, 2000)),
    ("unavailable holding limit leaves weekly status known", () => AssertTome(EligibilityStatus.Eligible,
        EligibilityStatus.Unknown, null, 20, 0, 900, 1000, null)),
    ("negative held count leaves weekly status known", () => AssertTome(EligibilityStatus.Eligible,
        EligibilityStatus.Unknown, null, 20, 0, 900, -1, 2000)),
    ("unavailable weekly count stays unknown", () => AssertTome(EligibilityStatus.Unknown,
        EligibilityStatus.Unknown, null, null, null, 900, 1000, 2000)),
    ("single-run dispatch is only allowed before launch intent", () =>
    {
        if (!SingleRunPolicy.MayDispatch("PendingValidation") ||
            SingleRunPolicy.MayDispatch("LaunchIntentRecorded") ||
            SingleRunPolicy.MayDispatch("AwaitingDuty"))
            throw new InvalidOperationException("A run state could permit duplicate dispatch.");
    }),
    ("uncertain persisted launch requires reload recovery", () =>
    {
        if (SingleRunPolicy.StateAfterReload("LaunchIntentRecorded") != "RecoveryRequired" ||
            SingleRunPolicy.StateAfterReload("AwaitingDuty") != "RecoveryRequired" ||
            SingleRunPolicy.StateAfterReload("Completed") != "Completed")
            throw new InvalidOperationException("Reload recovery policy mismatch.");
    }),
    ("reward reconciliation requires expected exact delta", () =>
    {
        if (SingleRunPolicy.ClassifyReward(50, 50, false, false, true) != SingleRunPolicy.RewardObservation.Verified ||
            SingleRunPolicy.ClassifyReward(50, 20, false, false, true) != SingleRunPolicy.RewardObservation.Wait ||
            SingleRunPolicy.ClassifyReward(50, 20, true, false, true) != SingleRunPolicy.RewardObservation.Unverified ||
            SingleRunPolicy.ClassifyReward(50, 60, false, false, true) != SingleRunPolicy.RewardObservation.Unverified ||
            SingleRunPolicy.ClassifyReward(50, -1, false, false, true) != SingleRunPolicy.RewardObservation.Unverified)
            throw new InvalidOperationException("Reward delta classification mismatch.");
    }),
    ("reward reconciliation blocks reset and identity changes", () =>
    {
        if (SingleRunPolicy.ClassifyReward(50, 50, false, true, true) != SingleRunPolicy.RewardObservation.ResetCrossed ||
            SingleRunPolicy.ClassifyReward(50, 50, false, false, false) != SingleRunPolicy.RewardObservation.IdentityMismatch ||
            SingleRunPolicy.ClassifyReward(50, null, false, false, true) != SingleRunPolicy.RewardObservation.Wait)
            throw new InvalidOperationException("Reward boundary/identity classification mismatch.");
    }),
    ("run timeout policy", () =>
    {
        var start = DateTimeOffset.UtcNow;
        if (!SingleRunPolicy.TimedOut(start.AddMinutes(4), start, TimeSpan.FromMinutes(3)) ||
            SingleRunPolicy.TimedOut(start.AddMinutes(2), start, TimeSpan.FromMinutes(3)))
            throw new InvalidOperationException("Timeout policy mismatch.");
    }),
    ("bare mode inverse mapping", () =>
    {
        if (!SingleRunPolicy.BareModeFor(false) || SingleRunPolicy.BareModeFor(true))
            throw new InvalidOperationException("Loop-action option mapping mismatch.");
    }),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}: {error.Message}");
    }
}

return failures == 0 ? 0 : 1;

static EligibilityCheck Check(EligibilityStatus status) =>
    new("test", status, "observed", "required", "detail", "source");

static void AssertOverall(EligibilityStatus expected, params EligibilityCheck[] checks)
{
    var actual = new EligibilityReport(checks).Overall;
    if (actual != expected)
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void AssertTome(EligibilityStatus weekly, EligibilityStatus storage, int? room,
    int? payout, int? acquired, int? cap, int? held, int? heldCap)
{
    var result = TomeRewardPolicy.Evaluate(20, acquired, cap, held, heldCap);
    if (result.WeeklyStatus != weekly || result.StorageStatus != storage ||
        result.StorageRoom != room || result.ExpectedPayout != payout)
        throw new InvalidOperationException($"Unexpected result: {result}");
}
