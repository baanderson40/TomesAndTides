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
    ("weekly reset and period boundaries use UTC", () =>
    {
        var before = new DateTimeOffset(2026, 10, 20, 7, 59, 0, TimeSpan.Zero);
        var reset = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        if (FarmingSchedulePolicy.GetWeekStartUtc(before) != new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero) ||
            FarmingSchedulePolicy.GetWeekStartUtc(reset) != reset ||
            FarmingSchedulePolicy.GetPeriodStartUtc(reset.AddHours(23), reset) != reset ||
            FarmingSchedulePolicy.GetPeriodStartUtc(reset.AddDays(1), reset) != reset.AddDays(1) ||
            FarmingSchedulePolicy.GetFinishDeadlineUtc(reset, 7) != reset.AddDays(7))
            throw new InvalidOperationException("UTC weekly or period boundary mismatch.");
    }),
    ("runs divide evenly with early remainder", () =>
    {
        if (!FarmingSchedulePolicy.DistributeRuns(15, 5).SequenceEqual([3, 3, 3, 3, 3]) ||
            !FarmingSchedulePolicy.DistributeRuns(16, 5).SequenceEqual([4, 3, 3, 3, 3]) ||
            !FarmingSchedulePolicy.DistributeRuns(2, 5).SequenceEqual([1, 1, 0, 0, 0]))
            throw new InvalidOperationException("Period allocation distribution mismatch.");
    }),
    ("mid-period start uses remaining reset-aligned periods", () =>
    {
        var start = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var startedAt = start.AddHours(13);
        var firstPeriod = FarmingSchedulePolicy.GetPeriodStartUtc(startedAt, start);
        var periods = FarmingSchedulePolicy.CreatePeriods(firstPeriod, start.AddDays(3), 8);
        if (firstPeriod != start || periods.Count != 3 ||
            !periods.Select(period => period.AllocatedRuns).SequenceEqual([3, 3, 2]))
            throw new InvalidOperationException("Current-period allocation mismatch.");
    }),
    ("missed periods redistribute from current period", () =>
    {
        var weekStart = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var remaining = FarmingSchedulePolicy.CreatePeriods(weekStart.AddDays(4), weekStart.AddDays(7), 7);
        if (remaining.Count != 3 || !remaining.Select(period => period.AllocatedRuns).SequenceEqual([3, 2, 2]))
            throw new InvalidOperationException("Remaining-period redistribution mismatch.");
    }),
    ("remaining weekly runs use ceiling and cap", () =>
    {
        if (FarmingSchedulePolicy.RunsNeeded(150, 900, 50) != 15 ||
            FarmingSchedulePolicy.RunsNeeded(875, 900, 50) != 1 ||
            FarmingSchedulePolicy.RunsNeeded(900, 900, 50) != 0)
            throw new InvalidOperationException("Required run count mismatch.");
    }),
    ("verified operation credit is idempotent", () =>
    {
        var credited = new[] { "run-1" };
        if (FarmingSchedulePolicy.ShouldCreditOperation("run-1", credited) ||
            !FarmingSchedulePolicy.ShouldCreditOperation("run-2", credited))
            throw new InvalidOperationException("Operation credit policy duplicated or suppressed credit.");
    }),
    ("stop and deadline prevent period dispatch", () =>
    {
        var now = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var deadline = now.AddDays(1);
        if (FarmingSchedulePolicy.GetQueueDecision(true, true, now, deadline, deadline, 0, 2) != FarmingSchedulePolicy.QueueDecision.PlanStopped ||
            FarmingSchedulePolicy.GetQueueDecision(true, false, deadline, deadline, deadline, 0, 2) != FarmingSchedulePolicy.QueueDecision.DeadlineReached ||
            FarmingSchedulePolicy.GetQueueDecision(true, false, now, deadline, deadline, 2, 2) != FarmingSchedulePolicy.QueueDecision.PeriodComplete ||
            FarmingSchedulePolicy.GetQueueDecision(true, false, now, deadline, deadline, 1, 2) != FarmingSchedulePolicy.QueueDecision.Queue)
            throw new InvalidOperationException("Stop/deadline/period dispatch decision mismatch.");
    }),
    ("reload resume requires reconciled safe operation", () =>
    {
        var credited = new[] { "run-1" };
        if (!FarmingSchedulePolicy.MayResumeAfterReload(null, null, false, true, null, credited) ||
            !FarmingSchedulePolicy.MayResumeAfterReload(null, null, false, true, "run-1", credited) ||
            FarmingSchedulePolicy.MayResumeAfterReload(null, null, false, true, "run-2", credited) ||
            !FarmingSchedulePolicy.MayResumeAfterReload("Completed", "Verified", true, true, "run-2", credited) ||
            FarmingSchedulePolicy.MayResumeAfterReload("Completed", "Unverified", true, true, "run-2", credited) ||
            FarmingSchedulePolicy.MayResumeAfterReload("RecoveryRequired", "Unknown", true, true, "run-2", credited) ||
            FarmingSchedulePolicy.MayResumeAfterReload("Failed", "Unknown", true, false, "run-2", credited))
            throw new InvalidOperationException("Reload recovery admission mismatch.");
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
