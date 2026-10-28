using TomesAndTides.Integrations;

namespace TomesAndTides.Debugging;

/// <summary>Applies explicit user Stop on a framework update; it never waits for provider settlement or character return.</summary>
public sealed class ManualStopController(
    Configuration configuration,
    Action save,
    AutomationSessionController automationSession,
    FarmingPlanController farmingPlan,
    DungeonRunController dungeonRun,
    RetainerHandoffController retainerHandoff,
    OceanFishingHandoffController oceanFishing,
    Func<bool?> readAutoDutyStopped,
    IWorkflowLog log,
    IUtcClock? clock = null)
{
    private readonly IUtcClock utcClock = clock ?? SystemUtcClock.Instance;
    private bool requested;

    public void RequestStop() => requested = true;

    /// <returns>True when this framework update consumed a manual Stop request.</returns>
    public bool Tick()
    {
        if (!requested)
            return false;
        requested = false;

        var warnings = new List<string>();
        var now = utcClock.UtcNow;
        if (configuration.DungeonRun is { State: "Completed", RewardOutcome: "Verified", FarmingPlanId: not null })
            farmingPlan.Tick(externalCharacterHandoffOwns: true, allowDispatch: false);
        var session = configuration.AutomationSession;
        if (session is not null)
        {
            session.State = "StopRequested";
            session.StopRequested = true;
            session.RecoveryKind = "ManualStop";
            session.Detail = "Manual Stop recorded; cancelling TT-owned work without waiting for farming-character return.";
        }
        if (configuration.RetainerSession is { } retainerSession &&
            (session is null || retainerSession.SessionId == session.SessionId))
        {
            retainerSession.State = "StopRequested";
            retainerSession.StopRequested = true;
            retainerSession.RecoveryKind = "ManualStop";
            retainerSession.Detail = "Manual Stop recorded; no further retainer work will be admitted.";
        }
        if (configuration.FarmingPlan is { } plan)
        {
            plan.StopRequested = true;
            plan.RecoveryKind = "ManualStop";
            plan.Detail = "Manual Stop recorded; cancelling pending work.";
        }
        if (configuration.DungeonRun is { State: "PendingValidation" } run)
        {
            run.State = "ManualStopPending";
            run.Detail = "Manual Stop recorded; no reward will be credited for an interrupted run.";
        }
        if (configuration.RetainerHandoff is { State: not ("Completed" or "RecoveryAcknowledged") } handoff)
        {
            handoff.State = "ManualStopPending";
            handoff.RecoveryKind = "ManualStop";
            handoff.Detail = "Manual Stop recorded; cancelling TT-owned provider work.";
            handoff.PhaseChangedUtc = now;
        }
        if (configuration.OceanFishingOperation is { State: not ("Completed" or "Skipped" or "RecoveryAcknowledged") } fishing)
        {
            fishing.State = "ManualStopPending";
            fishing.RecoveryKind = "ManualStop";
            fishing.Detail = "Manual Stop recorded; no relaunch or return request will be issued.";
            fishing.PhaseChangedUtc = now;
        }

        TrySave(warnings, "manual Stop intent");

        try { warnings.AddRange(retainerHandoff.CancelOwnedProviderWorkForManualStop()); }
        catch (Exception error) { warnings.Add($"AutoRetainer/Lifestream cancellation failed: {error.GetBaseException().Message}"); }
        try { warnings.AddRange(oceanFishing.CancelOwnedProviderWorkForManualStop()); }
        catch (Exception error) { warnings.Add($"WahTools/Lifestream cancellation failed: {error.GetBaseException().Message}"); }

        bool? autoDutyStopped;
        try { autoDutyStopped = readAutoDutyStopped(); }
        catch (Exception error)
        {
            autoDutyStopped = null;
            warnings.Add($"AutoDuty state read failed: {error.GetBaseException().Message}");
        }
        if (autoDutyStopped != true)
            warnings.Add(autoDutyStopped is false
                ? "AutoDuty was not stopped; TT does not pop its global overrides or abandon an active duty. AutoDuty may continue."
                : "AutoDuty state is unavailable; TT did not attempt its global stop path. Verify before starting again.");

        // Cancel only a not-yet-dispatched request. An active TT duty remains attached to its
        // observer so completion can be credited, but Stop prevents any follow-up scheduling.
        if (configuration.DungeonRun is { State: "ManualStopPending" })
            dungeonRun.CancelByUser("Cancelled by manual automation Stop before dispatch.");
        var detail = warnings.Count == 0
            ? "Stopped manually. Owned cancellation requests were issued where available; no farming-character return requested."
            : $"Stopped manually. {string.Join(" ", warnings)} No farming-character return requested.";

        retainerHandoff.CompleteManualStop(detail);
        oceanFishing.CompleteManualStop(detail);
        farmingPlan.CompleteManualStop(detail);
        automationSession.CompleteManualStop(detail);

        if (!TrySave(warnings, "manual Stop completion"))
        {
            detail += " TT stop could not be persisted; verify automation state after reload.";
            automationSession.CompleteManualStop(detail);
            farmingPlan.CompleteManualStop(detail);
            retainerHandoff.CompleteManualStop(detail);
            oceanFishing.CompleteManualStop(detail);
        }
        log.Warning("Manual automation Stop completed. Provider warnings: {Warnings}",
            warnings.Count == 0 ? "none" : string.Join(" | ", warnings));
        return true;
    }

    private bool TrySave(List<string> warnings, string purpose)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception error)
        {
            warnings.Add($"{purpose} could not be persisted: {error.GetBaseException().Message}");
            log.Error(error, "Manual Stop persistence failed during {Purpose}; in-memory dispatch remains stopped.", purpose);
            return false;
        }
    }
}
