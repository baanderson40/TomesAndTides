namespace TomesAndTides.Debugging;

/// <summary>Pure UTC period calculations for the manually started weekly farming plan.</summary>
public static class FarmingSchedulePolicy
{
    public enum QueueDecision { Queue, PlanStopped, DeadlineReached, PeriodComplete }

    public static readonly TimeSpan PeriodLength = TimeSpan.FromHours(24);

    public static DateTimeOffset GetWeekStartUtc(DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        var daysSinceTuesday = ((int)now.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 8, 0, 0, TimeSpan.Zero)
            .AddDays(-daysSinceTuesday);
        return now < start ? start.AddDays(-7) : start;
    }

    public static DateTimeOffset GetResetUtc(DateTimeOffset now) => GetWeekStartUtc(now).AddDays(7);

    public static DateTimeOffset GetPeriodStartUtc(DateTimeOffset now, DateTimeOffset weekStartUtc)
    {
        now = now.ToUniversalTime();
        weekStartUtc = weekStartUtc.ToUniversalTime();
        if (now < weekStartUtc)
            throw new ArgumentOutOfRangeException(nameof(now), "Timestamp precedes farming week start.");
        var day = (long)Math.Floor((now - weekStartUtc).TotalDays);
        return weekStartUtc.AddDays(day);
    }

    public static int GetPeriodIndex(DateTimeOffset periodStartUtc, DateTimeOffset weekStartUtc) =>
        checked((int)((periodStartUtc.ToUniversalTime() - weekStartUtc.ToUniversalTime()).Ticks / PeriodLength.Ticks));

    public static DateTimeOffset GetFinishDeadlineUtc(DateTimeOffset weekStartUtc, int periodsToUse)
    {
        if (periodsToUse is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(periodsToUse), "Period count must be 1 through 7.");
        return weekStartUtc.ToUniversalTime().AddDays(periodsToUse);
    }

    public static int RunsNeeded(int weeklyAcquired, int weeklyLimit, int reward)
    {
        if (weeklyAcquired < 0 || weeklyLimit <= 0 || reward <= 0)
            throw new ArgumentOutOfRangeException(nameof(reward), "Weekly counts, cap, and reward must be valid.");
        var remaining = Math.Max(0, weeklyLimit - weeklyAcquired);
        return remaining == 0 ? 0 : checked((remaining + reward - 1) / reward);
    }

    public static bool ShouldCreditOperation(string operationId, IReadOnlyCollection<string> creditedOperationIds) =>
        !creditedOperationIds.Contains(operationId, StringComparer.Ordinal);

    public static bool MayResumeAfterReload(string? runState, string? rewardOutcome,
        bool linkedRun, bool runSafelyAcknowledgable, string? currentOperationId,
        IReadOnlyCollection<string> creditedOperationIds)
    {
        if (runState is null)
            return currentOperationId is null || creditedOperationIds.Contains(currentOperationId, StringComparer.Ordinal);
        if (!linkedRun || !runSafelyAcknowledgable)
            return false;
        return runState == "Failed" || runState == "Completed" && rewardOutcome == "Verified";
    }

    public static QueueDecision GetQueueDecision(bool planActive, bool stopRequested,
        DateTimeOffset nowUtc, DateTimeOffset finishDeadlineUtc, DateTimeOffset resetUtc,
        int periodCompletedRuns, int periodAllocatedRuns)
    {
        if (!planActive || stopRequested)
            return QueueDecision.PlanStopped;
        if (nowUtc.ToUniversalTime() >= finishDeadlineUtc.ToUniversalTime() ||
            nowUtc.ToUniversalTime() >= resetUtc.ToUniversalTime())
            return QueueDecision.DeadlineReached;
        return periodCompletedRuns < periodAllocatedRuns ? QueueDecision.Queue : QueueDecision.PeriodComplete;
    }

    public static IReadOnlyList<int> DistributeRuns(int runs, int periodCount)
    {
        if (runs < 0)
            throw new ArgumentOutOfRangeException(nameof(runs));
        if (periodCount is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(periodCount));
        var result = new int[periodCount];
        var baseRuns = runs / periodCount;
        var remainder = runs % periodCount;
        for (var index = 0; index < result.Length; index++)
            result[index] = baseRuns + (index < remainder ? 1 : 0);
        return result;
    }

    public static IReadOnlyList<(DateTimeOffset StartUtc, int AllocatedRuns)> CreatePeriods(
        DateTimeOffset firstPeriodStartUtc, DateTimeOffset finishDeadlineUtc, int runs)
    {
        firstPeriodStartUtc = firstPeriodStartUtc.ToUniversalTime();
        finishDeadlineUtc = finishDeadlineUtc.ToUniversalTime();
        if (finishDeadlineUtc <= firstPeriodStartUtc)
            throw new ArgumentOutOfRangeException(nameof(finishDeadlineUtc), "No farming periods remain before deadline.");
        var count = checked((int)Math.Ceiling((finishDeadlineUtc - firstPeriodStartUtc).Ticks / (double)PeriodLength.Ticks));
        if (count is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(finishDeadlineUtc), "Available period count must be 1 through 7.");
        var allocation = DistributeRuns(runs, count);
        return Enumerable.Range(0, count)
            .Select(index => (firstPeriodStartUtc.AddDays(index), allocation[index]))
            .ToArray();
    }
}
