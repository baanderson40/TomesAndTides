namespace TomesAndTides.Debugging;

public readonly record struct OceanFishingWindow(DateTimeOffset StartUtc, DateTimeOffset RegistrationEndUtc,
    DateTimeOffset LaunchCutoffUtc, DateTimeOffset PreparationStartUtc);

public static class OceanFishingSchedulePolicy
{
    public static readonly TimeSpan RegistrationLength = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan WindowInterval = TimeSpan.FromHours(2);
    public static readonly TimeSpan LaunchCutoffOffset = TimeSpan.FromMinutes(13);

    public static OceanFishingWindow GetWindow(DateTimeOffset nowUtc, int preparationLeadMinutes)
    {
        var now = nowUtc.ToUniversalTime();
        var evenHour = now.Hour - now.Hour % 2;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, evenHour, 0, 0, TimeSpan.Zero);
        if (now >= start.Add(RegistrationLength))
            start = start.Add(WindowInterval);
        var lead = TimeSpan.FromMinutes(Math.Clamp(preparationLeadMinutes, 0, 45));
        return new(start, start.Add(RegistrationLength), start.Add(LaunchCutoffOffset), start - lead);
    }

    public static bool IsPreparationEligible(DateTimeOffset nowUtc, OceanFishingWindow window) =>
        nowUtc.ToUniversalTime() >= window.PreparationStartUtc && nowUtc.ToUniversalTime() < window.LaunchCutoffUtc;

    public static bool IsWindowHandled(IEnumerable<DateTimeOffset> handledWindows, DateTimeOffset windowStartUtc) =>
        handledWindows.Any(handled => handled.ToUniversalTime() == windowStartUtc.ToUniversalTime());
}

public static class AutomationPriorityPolicy
{
    public static string? SelectNext(bool ownedActivity, bool stopRequested, bool fishingWindowEligible,
        bool fishingEnabled, bool retainerDue, bool retainersEnabled, bool dungeonDue) =>
        ownedActivity || stopRequested ? null :
        fishingEnabled && fishingWindowEligible ? "OceanFishing" :
        retainersEnabled && retainerDue ? "Retainers" :
        dungeonDue ? "Dungeon" : null;
}
