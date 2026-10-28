namespace TomesAndTides.Debugging;

/// <summary>Persisted framework-thread scheduler with one current-period dungeon checklist.</summary>
public sealed class FarmingPlanController(
    Configuration configuration,
    Action save,
    DungeonRunController dungeonRun,
    IWorkflowGameState gameState,
    AutomationSessionController automationSession,
    IWorkflowLog log,
    Func<DebugTests.LiveTomeState> readTomeState,
    Func<uint, DebugTests.DutyModeSelection, int, DebugReport> eligibility,
    Func<uint, DebugTests.DungeonOption?> findDungeon,
    IUtcClock? clock = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BlockerRetry = TimeSpan.FromSeconds(30);
    private DateTimeOffset nextRefreshUtc;
    private IUtcClock Clock { get; } = clock ?? SystemUtcClock.Instance;

    public FarmingPlanRecord? Current => configuration.FarmingPlan;
    public FarmingPeriodRecord? CurrentChecklist
    {
        get
        {
            var plan = Current;
            if (plan is null)
                return null;
            var currentStart = FarmingSchedulePolicy.GetPeriodStartUtc(Clock.UtcNow, plan.WeekStartUtc);
            return plan.Periods.FirstOrDefault(period => period.StartUtc == currentStart);
        }
    }
    public bool IsActive => Current is { State: "Active" or "PendingValidation" };
    public bool AllowsRetainerHandoffNow
    {
        get
        {
            var plan = Current;
            if (plan is null)
                return true;
            if (plan.State is "PendingValidation" or "RecoveryRequired")
                return false;
            if (plan.State is not "Active")
                return true;
            var now = Clock.UtcNow;
            if (plan.StopRequested || now >= plan.FinishDeadlineUtc || now >= plan.WeeklyResetUtc)
                return true;
            var periodStart = FarmingSchedulePolicy.GetPeriodStartUtc(now, plan.WeekStartUtc);
            var period = plan.Periods.FirstOrDefault(item => item.StartUtc == periodStart);
            return period is not null && period.CompletedRuns >= period.AllocatedRuns;
        }
    }
    public bool BlocksManualRun => Current is { State: "Active" or "PendingValidation" or "RecoveryRequired" };
    public bool CanStart
    {
        get
        {
            var plan = Current;
            if (plan is null)
                return true;
            if (plan.State is "Active" or "PendingValidation" or "RecoveryRequired")
                return false;
            var currentWeek = FarmingSchedulePolicy.GetWeekStartUtc(Clock.UtcNow);
            if (plan.WeekStartUtc == currentWeek)
                return plan.State switch
                {
                    "Stopped" => Clock.UtcNow < plan.FinishDeadlineUtc,
                    "RecoveryAcknowledged" => Clock.UtcNow < plan.WeeklyResetUtc,
                    _ => false,
                };
            return true;
        }
    }
    public bool CanAcknowledgeRecovery => Current is { State: "RecoveryRequired" } plan &&
                                         (dungeonRun.Current is not { } run || run.FarmingPlanId != plan.PlanId || dungeonRun.CanAcknowledge);
    public bool CanResumeAfterReload
    {
        get
        {
            var plan = Current;
            if (plan is not { State: "RecoveryRequired", RecoveryKind: "Reload" })
                return false;
            if (configuration.RetainerHandoff is { State: "RecoveryRequired" })
                return false;
            if (configuration.AutomationSession is not { State: "RecoveryRequired", RecoveryKind: "Reload" } parent ||
                parent.FarmingPlanId != plan.PlanId ||
                configuration.OceanFishingOperation is { State: not ("Completed" or "Skipped" or "RecoveryAcknowledged") })
                return false;
            var run = dungeonRun.Current;
            return FarmingSchedulePolicy.MayResumeAfterReload(run?.State, run?.RewardOutcome,
                run is null || run.FarmingPlanId == plan.PlanId && run.OperationId == plan.CurrentOperationId,
                run is null || dungeonRun.CanAcknowledge,
                plan.CurrentOperationId, plan.CreditedOperationIds);
        }
    }

    public void RecoverAfterReload()
    {
        var plan = Current;
        if (plan is { RecoveryKind: "ManualStop" })
        {
            CompleteManualStop("Manual Stop was persisted before reload; no plan work will resume.");
            SavePlan(plan, "recover persisted manual Stop");
            return;
        }
        if (plan is not { State: "Active" or "PendingValidation" })
            return;
        plan.State = "RecoveryRequired";
        plan.RecoveryKind = "Reload";
        plan.Detail = "Plugin reloaded during an active plan. Inspect the run/provider state, then explicitly resume or stop.";
        SavePlan(plan, "reload recovery");
        log.Warning("Farming plan {PlanId} requires reload recovery; automatic dispatch paused.", plan.PlanId);
    }

    public bool QueueStart(DebugTests.DungeonOption dungeon, DebugTests.DutyModeSelection mode,
        int gearsetId, bool bareMode, int periodsToUse, ulong expectedCharacterContentId,
        string farmingCharacterName, string farmingCharacterWorld, bool serviceRetainers, int retainerWaitMinutes)
    {
        if (periodsToUse is < 1 or > 7)
            throw new InvalidOperationException("Choose between 1 and 7 weekly periods.");
        if (retainerWaitMinutes is < 0 or > 30)
            throw new InvalidOperationException("Retainer wait must be between 0 and 30 minutes.");

        var previousPlan = configuration.FarmingPlan;
        var previousRetainerSession = configuration.RetainerSession;
        if (configuration.RetainerHandoff is { State: not ("Completed" or "RecoveryAcknowledged") } ||
            previousRetainerSession is { State: "RecoveryRequired" })
            throw new InvalidOperationException("Resolve the existing retainer handoff/session before starting or resuming farming.");
        if (previousPlan is { State: "Stopped" } prior &&
            prior.WeekStartUtc == FarmingSchedulePolicy.GetWeekStartUtc(Clock.UtcNow) &&
            Clock.UtcNow < prior.FinishDeadlineUtc)
        {
            if (configuration.RetainerSession is { State: "Active" or "StopRequested" } legacySession &&
                legacySession.FarmingPlanId != prior.PlanId)
                throw new InvalidOperationException("Resolve the existing retainer handoff/session before resuming this farming plan.");
            if (prior.CharacterContentId != expectedCharacterContentId || prior.TerritoryId != dungeon.Territory ||
                prior.ContentFinderConditionId != dungeon.ContentFinderConditionId || prior.DungeonReward != dungeon.Reward ||
                !string.Equals(prior.Mode, mode.ToString(), StringComparison.Ordinal) ||
                prior.GearsetInspectionId != gearsetId || prior.BareMode != bareMode ||
                prior.FinishDeadlineUtc != FarmingSchedulePolicy.GetFinishDeadlineUtc(prior.WeekStartUtc, periodsToUse))
                throw new InvalidOperationException("Stopped plan can resume only with its original character, dungeon, mode, bare-mode, and finish period.");
            prior.ServiceRetainers = serviceRetainers;
            prior.RetainerWaitBeforeDungeonMinutes = retainerWaitMinutes;
            prior.FarmingCharacterName = farmingCharacterName;
            prior.FarmingCharacterWorld = farmingCharacterWorld;
            prior.StopRequested = false;
            prior.State = prior.Periods.Count == 0 ? "PendingValidation" : "Active";
            prior.Detail = prior.Periods.Count == 0
                ? "Plan start validation resumed; no period progress existed to preserve."
                : "Plan resumed; completed period progress is preserved.";
            prior.Blocker = null;
            prior.RetryAfterUtc = null;
            configuration.RetainerSession = ResumeLinkedRetainerSession(prior, farmingCharacterName, farmingCharacterWorld, serviceRetainers);
            automationSession.StartOrResume(expectedCharacterContentId, farmingCharacterName, farmingCharacterWorld,
                serviceRetainers, retainerWaitMinutes, configuration.OceanFishingEnabled,
                Math.Clamp(configuration.OceanFishingPreparationLeadMinutes, 0, 45), configuration.OceanFishingCharacterMode,
                configuration.OceanFishingCharacterContentId, configuration.OceanFishingCharacterName,
                configuration.OceanFishingCharacterWorld, prior.PlanId);
            if (!SavePlan(prior, "resume farming plan"))
                throw new InvalidOperationException("Could not persist farming plan resume; recovery is required.");
            nextRefreshUtc = DateTimeOffset.MinValue;
            log.Information("Farming plan {PlanId} resumed; completed period ledger preserved, retainer service={ServiceRetainers}, waitMinutes={WaitMinutes}.",
                prior.PlanId, prior.ServiceRetainers, prior.RetainerWaitBeforeDungeonMinutes);
            return true;
        }
        if (!CanStart)
            throw new InvalidOperationException("A farming plan is already active or requires recovery.");
        if (previousRetainerSession is { State: "Active" or "StopRequested" })
            throw new InvalidOperationException("Stop or recover the existing combined automation session before starting a new plan.");

        var plan = new FarmingPlanRecord
        {
            PlanId = Guid.NewGuid().ToString("N"),
            State = "PendingValidation",
            Detail = "Confirmed; waiting for fresh framework-thread validation.",
            TerritoryId = dungeon.Territory,
            ContentFinderConditionId = dungeon.ContentFinderConditionId,
            DungeonName = dungeon.Name,
            Mode = mode.ToString(),
            GearsetInspectionId = gearsetId,
            BareMode = bareMode,
            ServiceRetainers = serviceRetainers,
            RetainerWaitBeforeDungeonMinutes = retainerWaitMinutes,
            FarmingCharacterName = farmingCharacterName,
            FarmingCharacterWorld = farmingCharacterWorld,
            DungeonReward = dungeon.Reward,
            RequestedPeriodCount = periodsToUse,
            CharacterContentId = expectedCharacterContentId,
            CreatedUtc = Clock.UtcNow,
        };
        plan.WeekStartUtc = FarmingSchedulePolicy.GetWeekStartUtc(plan.CreatedUtc);
        plan.WeeklyResetUtc = plan.WeekStartUtc.AddDays(7);
        plan.FinishDeadlineUtc = FarmingSchedulePolicy.GetFinishDeadlineUtc(plan.WeekStartUtc, periodsToUse);
        configuration.FarmingPlan = plan;
        configuration.RetainerSession = CreateLinkedRetainerSession(plan, farmingCharacterName, farmingCharacterWorld, serviceRetainers);
        automationSession.StartOrResume(expectedCharacterContentId, farmingCharacterName, farmingCharacterWorld,
            serviceRetainers, retainerWaitMinutes, configuration.OceanFishingEnabled,
            Math.Clamp(configuration.OceanFishingPreparationLeadMinutes, 0, 45), configuration.OceanFishingCharacterMode,
            configuration.OceanFishingCharacterContentId, configuration.OceanFishingCharacterName,
            configuration.OceanFishingCharacterWorld, plan.PlanId);
        var archivedPrior = previousPlan is not null &&
                            (previousPlan.WeekStartUtc != plan.WeekStartUtc || previousPlan.State == "RecoveryAcknowledged");
        if (archivedPrior)
        {
            configuration.FarmingPlanHistory ??= [];
            configuration.FarmingPlanHistory.Add(previousPlan!);
        }
        try
        {
            save();
            log.Information("Farming plan {PlanId} confirmed for {Dungeon}, mode={Mode}, periods={Periods}, bareMode={BareMode}.",
                plan.PlanId, plan.DungeonName, plan.Mode, periodsToUse, plan.BareMode);
            return true;
        }
        catch (Exception error)
        {
            configuration.FarmingPlan = previousPlan;
            configuration.RetainerSession = previousRetainerSession;
            if (archivedPrior)
                configuration.FarmingPlanHistory.Remove(previousPlan!);
            log.Error(error, "Farming plan {PlanId} confirmation could not persist; no plan started.", plan.PlanId);
            throw;
        }
    }

    public void RequestStop(bool preserveOwnedRetainerCheckpoint = false)
    {
        var plan = Current;
        if (plan is null || plan.State is not ("PendingValidation" or "Active"))
            return;
        plan.StopRequested = true;
        if (!preserveOwnedRetainerCheckpoint && plan.RetainerCheckpointOperationId is not null)
            plan.RetainerCheckpointOperationId = null;
        plan.Detail = "Stop requested; no further dungeon launch will be issued.";
        if (SavePlan(plan, "stop request"))
            log.Information("Farming plan {PlanId} Stop requested.", plan.PlanId);
    }

    public void CompleteManualStop(string detail)
    {
        if (Current is not { } plan)
            return;
        plan.State = "Stopped";
        plan.RecoveryKind = null;
        plan.StopRequested = true;
        if (dungeonRun.Current is not { State: "AwaitingDuty" or "RunningDuty" or "WaitingForExit" or "WaitingForReward" } activeRun ||
            activeRun.FarmingPlanId != plan.PlanId)
            plan.CurrentOperationId = null;
        plan.RetainerCheckpointOperationId = null;
        plan.Blocker = null;
        plan.RetryAfterUtc = null;
        plan.Detail = $"{detail} Period allocations and credited-run history preserved.";
        nextRefreshUtc = DateTimeOffset.MinValue;
    }

    public bool CompleteRetainerCheckpoint(string operationId, string detail)
    {
        var plan = Current;
        if (plan is null || plan.RetainerCheckpointOperationId != operationId)
            return false;
        plan.CompletedRetainerCheckpointOperationIds ??= [];
        if (!plan.CompletedRetainerCheckpointOperationIds.Contains(operationId, StringComparer.Ordinal))
            plan.CompletedRetainerCheckpointOperationIds.Add(operationId);
        plan.RetainerCheckpointOperationId = null;
        plan.Detail = detail;
        plan.RetryAfterUtc = null;
        nextRefreshUtc = DateTimeOffset.MinValue;
        return SavePlan(plan, "complete retainer checkpoint");
    }

    public bool AcknowledgeRecoveryAndStop(bool automationSafe)
    {
        var plan = Current;
        if (plan is null || !CanAcknowledgeRecovery || !automationSafe ||
            configuration.RetainerHandoff is { State: not ("Completed" or "RecoveryAcknowledged") } ||
            configuration.OceanFishingOperation is { State: not ("Completed" or "Skipped" or "RecoveryAcknowledged") })
            return false;
        if (dungeonRun.Current is { } linkedRun && linkedRun.FarmingPlanId == plan.PlanId && !dungeonRun.Acknowledge())
            return false;
        var previousPlanStopRequested = plan.StopRequested;
        var parent = configuration.AutomationSession;
        var retainerSession = configuration.RetainerSession;
        var previousParentState = parent?.State;
        var previousParentRecoveryKind = parent?.RecoveryKind;
        var previousParentDetail = parent?.Detail;
        var previousParentStopRequested = parent?.StopRequested;
        var previousRetainerState = retainerSession?.State;
        var previousRetainerRecoveryKind = retainerSession?.RecoveryKind;
        var previousRetainerDetail = retainerSession?.Detail;
        var previousRetainerStopRequested = retainerSession?.StopRequested;
        plan.State = "RecoveryAcknowledged";
        plan.RecoveryKind = "AcknowledgedStop";
        plan.StopRequested = true;
        plan.Detail = "Recovery acknowledged by user; this plan will not resume or retry. Automation stopped at a verified safe boundary.";
        plan.CurrentOperationId = null;
        if (parent is { State: "Active" or "StopRequested" or "RecoveryRequired" })
        {
            parent.State = "Stopped";
            parent.StopRequested = true;
            parent.RecoveryKind = null;
            parent.Detail = "Recovery acknowledged and automation stopped; no ambiguous request was replayed.";
        }
        if (retainerSession is { State: "Active" or "StopRequested" or "RecoveryRequired" })
        {
            retainerSession.State = "Stopped";
            retainerSession.StopRequested = true;
            retainerSession.RecoveryKind = null;
            retainerSession.Detail = "Automation recovery acknowledged and retainer session stopped safely.";
        }
        try
        {
            save();
            log.Information("Farming plan {PlanId} and linked automation recovery acknowledged and stopped.", plan.PlanId);
            return true;
        }
        catch (Exception error)
        {
            plan.State = "RecoveryRequired";
            plan.RecoveryKind = "PersistenceFailure";
            plan.Detail = "Could not persist recovery stop; automation remains blocked.";
            plan.StopRequested = previousPlanStopRequested;
            if (parent is not null)
            {
                parent.State = previousParentState!;
                parent.RecoveryKind = previousParentRecoveryKind;
                parent.Detail = previousParentDetail!;
                parent.StopRequested = previousParentStopRequested!.Value;
            }
            if (retainerSession is not null)
            {
                retainerSession.State = previousRetainerState!;
                retainerSession.RecoveryKind = previousRetainerRecoveryKind;
                retainerSession.Detail = previousRetainerDetail!;
                retainerSession.StopRequested = previousRetainerStopRequested!.Value;
            }
            log.Error(error, "Farming plan {PlanId} recovery stop could not be persisted; automation remains blocked.", plan.PlanId);
            return false;
        }
    }

    public bool ResumeAfterReload(bool automationSafe)
    {
        var plan = Current;
        if (plan is null || !CanResumeAfterReload || !automationSafe)
            return false;
        var previousState = plan.State;
        var previousDetail = plan.Detail;
        var previousRecoveryKind = plan.RecoveryKind;
        var previousStopRequested = plan.StopRequested;
        var previousCurrentOperationId = plan.CurrentOperationId;
        var previousBlocker = plan.Blocker;
        var previousRetryAfterUtc = plan.RetryAfterUtc;
        var priorRetainerSession = configuration.RetainerSession;
        var priorRetainerState = priorRetainerSession?.State;
        var priorRetainerDetail = priorRetainerSession?.Detail;
        var priorRetainerStopRequested = priorRetainerSession?.StopRequested;
        var priorRetainerRecoveryKind = priorRetainerSession?.RecoveryKind;
        var run = dungeonRun.Current;
        if (run is { State: "Completed", RewardOutcome: "Verified" } && !CreditVerifiedRun(plan, run))
            return false;
        if (run is not null && !dungeonRun.Acknowledge())
            return false;
        var rollbackAutomation = automationSession.PrepareReloadResume(plan.PlanId, plan.CharacterContentId,
            plan.FarmingCharacterName, plan.FarmingCharacterWorld);
        if (rollbackAutomation is null)
            return false;
        plan.CurrentOperationId = null;
        plan.RecoveryKind = null;
        plan.State = plan.Periods.Count == 0 ? "PendingValidation" : "Active";
        plan.StopRequested = false;
        plan.Blocker = null;
        plan.RetryAfterUtc = null;
        plan.Detail = "Reload recovery acknowledged; plan resumed after safe run reconciliation.";
        configuration.RetainerSession = ResumeLinkedRetainerSession(plan, plan.FarmingCharacterName,
            plan.FarmingCharacterWorld, plan.ServiceRetainers);
        nextRefreshUtc = DateTimeOffset.MinValue;
        try
        {
            save();
            log.Information("Farming plan {PlanId} and automation session {SessionId} resumed after reload; plan progress and captured settings preserved.",
                plan.PlanId, configuration.AutomationSession?.SessionId ?? "unknown");
            return true;
        }
        catch (Exception error)
        {
            plan.State = previousState;
            plan.Detail = previousDetail;
            plan.RecoveryKind = previousRecoveryKind;
            plan.StopRequested = previousStopRequested;
            plan.CurrentOperationId = previousCurrentOperationId;
            plan.Blocker = previousBlocker;
            plan.RetryAfterUtc = previousRetryAfterUtc;
            configuration.RetainerSession = priorRetainerSession;
            if (priorRetainerSession is not null)
            {
                priorRetainerSession.State = priorRetainerState!;
                priorRetainerSession.Detail = priorRetainerDetail!;
                priorRetainerSession.StopRequested = priorRetainerStopRequested!.Value;
                priorRetainerSession.RecoveryKind = priorRetainerRecoveryKind;
            }
            rollbackAutomation();
            log.Error(error, "Farming plan {PlanId} reload resume could not persist; all automation remains blocked.", plan.PlanId);
            return false;
        }
    }

    public void Tick(bool externalCharacterHandoffOwns = false, bool allowDispatch = true)
    {
        try
        {
            TickCore(externalCharacterHandoffOwns, allowDispatch);
        }
        catch (Exception error)
        {
            if (Current is { } plan)
            {
                plan.State = "RecoveryRequired";
                plan.Detail = $"Coordinator error; no further automatic launches. {error.GetBaseException().Message}";
                SavePlan(plan, "coordinator exception recovery");
                log.Error(error, "Farming plan {PlanId} coordinator failed; state set to RecoveryRequired.", plan.PlanId);
            }
        }
    }

    private void TickCore(bool externalCharacterHandoffOwns, bool allowDispatch)
    {
        var plan = Current;
        if (plan is null)
            return;

        if (plan.State == "Stopped")
        {
            ObserveStoppedRun(plan);
            return;
        }

        if (plan.State == "PendingValidation")
        {
            if (plan.StopRequested)
            {
                Transition(plan, "Stopped", "Plan cancelled before initialization or dispatch.");
                return;
            }
            if (externalCharacterHandoffOwns)
                return;
            if (plan.RetryAfterUtc is { } startRetry && Clock.UtcNow < startRetry)
                return;
            Initialize(plan);
            return;
        }
        if (plan.State is not ("Active" or "RecoveryRequired"))
            return;

        var run = dungeonRun.Current;
        if (run is not null && string.Equals(run.FarmingPlanId, plan.PlanId, StringComparison.Ordinal))
        {
            if (plan.CurrentOperationId is null)
            {
                plan.CurrentOperationId = run.OperationId;
                if (!SavePlan(plan, "link recovered farming run"))
                    return;
            }
            // Legacy weekly plans may already contain today's period while their saved pointer
            // still points at yesterday. The active run's immutable period link is authoritative.
            if (run.FarmingPeriodStartUtc is { } activePeriod &&
                activePeriod == FarmingSchedulePolicy.GetPeriodStartUtc(Clock.UtcNow, plan.WeekStartUtc) &&
                plan.Periods.FirstOrDefault(period => period.StartUtc == activePeriod) is { } activeChecklist &&
                (plan.CurrentPeriodStartUtc != activePeriod || plan.Periods.Count != 1))
            {
                plan.CurrentPeriodStartUtc = activePeriod;
                plan.Periods = [activeChecklist];
                if (!SavePlan(plan, "repair current checklist pointer from active run"))
                    return;
                log.Information("Farming plan {PlanId} repaired current checklist pointer from active run {OperationId}: period={PeriodStartUtc}, progress={Completed}/{Target}.",
                    plan.PlanId, run.OperationId, activePeriod, activeChecklist.CompletedRuns, activeChecklist.AllocatedRuns);
            }
            if (run.State == "RecoveryRequired")
            {
                Transition(plan, "RecoveryRequired", $"Run {run.OperationId} requires explicit recovery. {run.Detail}");
                return;
            }
            if (run.State == "Completed")
            {
                if (run.RewardOutcome != "Verified")
                {
                    Transition(plan, "RecoveryRequired", $"Run {run.OperationId} ended with reward outcome {run.RewardOutcome}; no automatic continuation.");
                    return;
                }
                if (!CreditVerifiedRun(plan, run))
                    return;
                if (!dungeonRun.CanAcknowledge || !dungeonRun.Acknowledge())
                    return;
                plan.CurrentOperationId = null;
                if (!SavePlan(plan, "release verified run"))
                    return;
            }
            else if (run.State == "Failed")
            {
                plan.Blocker = run.Detail;
                plan.RetryAfterUtc = Clock.UtcNow.Add(BlockerRetry);
                log.Warning("Farming plan {PlanId} launch validation blocked before dispatch; retry after {RetryUtc}: {Blocker}",
                    plan.PlanId, plan.RetryAfterUtc, plan.Blocker);
                if (!dungeonRun.CanAcknowledge || !dungeonRun.Acknowledge())
                    return;
                plan.CurrentOperationId = null;
                SavePlan(plan, "release blocked pre-dispatch run");
                return;
            }
            else if (run.State == "PendingValidation" &&
                     (plan.StopRequested || Clock.UtcNow >= plan.FinishDeadlineUtc || Clock.UtcNow >= plan.WeeklyResetUtc))
            {
                if (!dungeonRun.CancelPendingPlanRun(plan.PlanId))
                    return;
                plan.CurrentOperationId = null;
                if (!SavePlan(plan, "cancel pending run at plan boundary"))
                    return;
            }
            else if (run.State == "PendingValidation" && run.FarmingPeriodStartUtc is { } queuedPeriod &&
                     FarmingSchedulePolicy.GetPeriodStartUtc(Clock.UtcNow, plan.WeekStartUtc) != queuedPeriod)
            {
                if (!dungeonRun.CancelPendingPlanRun(plan.PlanId))
                    return;
                plan.CurrentOperationId = null;
                if (!SavePlan(plan, "cancel stale-period pending run"))
                    return;
            }
            else
                return;
        }
        else if (run is not null)
        {
            // Never take over an unrelated manually confirmed run.
            plan.Blocker = "Another single-run operation owns AutoDuty.";
            return;
        }

        if (plan.State == "RecoveryRequired")
            return;
        if (plan.StopRequested)
        {
            if (dungeonRun.Current is { State: "PendingValidation", FarmingPlanId: { } pendingPlan } &&
                pendingPlan == plan.PlanId)
            {
                if (!dungeonRun.CancelPendingPlanRun(plan.PlanId))
                    return;
                plan.CurrentOperationId = null;
            }
            Transition(plan, "Stopped", "Stop completed at safe boundary; period ledger preserved.");
            return;
        }

        var now = Clock.UtcNow;
        if (now >= plan.FinishDeadlineUtc && now < plan.WeeklyResetUtc)
        {
            SetWaiting(plan, "Dungeon checklist finished for this schedule; retainers and fishing remain active until weekly reset.");
            return;
        }
        if (externalCharacterHandoffOwns)
            return;
        if (plan.RetainerCheckpointOperationId is not null)
            return;
        if (Clock.UtcNow < nextRefreshUtc || plan.RetryAfterUtc is { } retryAfter && now < retryAfter)
            return;
        nextRefreshUtc = now.Add(RefreshInterval);

        if (gameState.ContentId == 0 || gameState.ContentId != plan.CharacterContentId)
        {
            SetBlocker(plan, "Waiting for the farming character to return before continuing the checklist.");
            return;
        }

        DebugTests.LiveTomeState tome;
        try { tome = readTomeState(); }
        catch (Exception error)
        {
            SetBlocker(plan, $"Live tome state unknown: {error.GetBaseException().Message}");
            return;
        }
        if (tome.CharacterContentId != plan.CharacterContentId)
        {
            SetBlocker(plan, "Waiting for fresh tome state for the farming character.");
            return;
        }
        var currentWeekStart = FarmingSchedulePolicy.GetWeekStartUtc(now);
        if (currentWeekStart != plan.WeekStartUtc)
        {
            plan.WeekStartUtc = currentWeekStart;
            plan.WeeklyResetUtc = currentWeekStart.AddDays(7);
            plan.FinishDeadlineUtc = FarmingSchedulePolicy.GetFinishDeadlineUtc(currentWeekStart, plan.RequestedPeriodCount);
            plan.CurrentPeriodStartUtc = null;
            plan.Periods = [];
            plan.WeeklyAcquiredAtStart = tome.WeeklyAcquired;
            plan.WeeklyLimitAtStart = tome.WeeklyLimit;
            plan.CurrencyItemId = tome.CurrencyItemId;
            plan.Detail = "Weekly reset detected; calculating a fresh current-period checklist.";
            plan.Blocker = null;
            if (!SavePlan(plan, "begin new weekly checklist"))
                return;
            log.Information("Farming plan {PlanId} detected weekly reset {WeekStartUtc}; current-period checklist will be recalculated.",
                plan.PlanId, currentWeekStart);
        }
        if (plan.WeeklyLimitAtStart != tome.WeeklyLimit || plan.CurrencyItemId != tome.CurrencyItemId)
        {
            plan.WeeklyLimitAtStart = tome.WeeklyLimit;
            plan.CurrencyItemId = tome.CurrencyItemId;
            SavePlan(plan, "refresh weekly tome identity");
        }
        var runsNeeded = FarmingSchedulePolicy.RunsNeeded(tome.WeeklyAcquired, tome.WeeklyLimit, plan.DungeonReward);
        if (runsNeeded == 0)
        {
            SetWaiting(plan, "Weekly tome cap reached; dungeon checklist is paused until weekly reset.");
            return;
        }

        var currentPeriodStart = FarmingSchedulePolicy.GetPeriodStartUtc(now, plan.WeekStartUtc);
        if (currentPeriodStart >= plan.FinishDeadlineUtc)
        {
            SetWaiting(plan, "Dungeon checklist finished for this schedule; retainers and fishing remain active until weekly reset.");
            return;
        }

        var currentPeriod = plan.Periods.FirstOrDefault(period => period.StartUtc == currentPeriodStart);
        if (currentPeriod is null)
        {
            var target = FarmingSchedulePolicy.CurrentPeriodTarget(runsNeeded, currentPeriodStart, plan.FinishDeadlineUtc);
            currentPeriod = new FarmingPeriodRecord { StartUtc = currentPeriodStart, AllocatedRuns = target };
            plan.CurrentPeriodStartUtc = currentPeriodStart;
            plan.Periods = [currentPeriod];
            plan.Blocker = null;
            plan.RetryAfterUtc = null;
            plan.Detail = $"Current-period checklist created: 0/{target} runs.";
            if (!SavePlan(plan, "create current-period checklist"))
                return;
            log.Information("Farming plan {PlanId} checklist created for {PeriodStartUtc}: target={Target}, weeklyRunsRemaining={RunsRemaining}, finish={FinishUtc}.",
                plan.PlanId, currentPeriodStart, target, runsNeeded, plan.FinishDeadlineUtc);
        }
        else
        {
            var pointerIsStale = plan.CurrentPeriodStartUtc != currentPeriodStart;
            var checklistHasProgress = currentPeriod.CompletedRuns > 0 || (currentPeriod.CreditedOperationIds?.Count ?? 0) > 0;
            var hasLegacyFutureAllocations = plan.Periods.Count > 1;
            var recalculateUntouchedTarget = !checklistHasProgress && (pointerIsStale || hasLegacyFutureAllocations);
            if (recalculateUntouchedTarget)
                currentPeriod.AllocatedRuns = FarmingSchedulePolicy.CurrentPeriodTarget(runsNeeded, currentPeriodStart, plan.FinishDeadlineUtc);

            var requiresMigration = pointerIsStale || plan.Periods.Count != 1;
            if (requiresMigration)
            {
                plan.CurrentPeriodStartUtc = currentPeriodStart;
                plan.Periods = [currentPeriod];
                plan.Detail = checklistHasProgress
                    ? $"Current-period checklist restored: {currentPeriod.CompletedRuns}/{currentPeriod.AllocatedRuns} runs."
                    : $"Current-period checklist calculated: 0/{currentPeriod.AllocatedRuns} runs.";
                plan.Blocker = null;
                plan.RetryAfterUtc = null;
                if (!SavePlan(plan, "migrate single current-period checklist"))
                    return;
                log.Information("Farming plan {PlanId} migrated current checklist: period={PeriodStartUtc}, progress={Completed}/{Target}, recalculated={Recalculated}.",
                    plan.PlanId, currentPeriodStart, currentPeriod.CompletedRuns, currentPeriod.AllocatedRuns, recalculateUntouchedTarget);
            }
        }
        if (currentPeriod.CompletedRuns >= currentPeriod.AllocatedRuns)
        {
            SetWaiting(plan, $"Checklist complete: {currentPeriod.CompletedRuns}/{currentPeriod.AllocatedRuns}. Next period starts {currentPeriodStart.AddDays(1):O} UTC.");
            plan.RetryAfterUtc = null;
            nextRefreshUtc = currentPeriodStart.AddDays(1);
            return;
        }
        if (FarmingSchedulePolicy.GetQueueDecision(plan.State == "Active", plan.StopRequested,
                now, plan.FinishDeadlineUtc, plan.WeeklyResetUtc,
                currentPeriod.CompletedRuns, currentPeriod.AllocatedRuns) != FarmingSchedulePolicy.QueueDecision.Queue)
            return;
        if (!allowDispatch)
        {
            nextRefreshUtc = DateTimeOffset.MinValue;
            return;
        }

        var dungeon = findDungeon(plan.TerritoryId);
        if (dungeon is null || dungeon.ContentFinderConditionId != plan.ContentFinderConditionId || dungeon.Reward != plan.DungeonReward)
        {
            SetBlocker(plan, "Selected dungeon data is unavailable or changed; no run queued.");
            return;
        }
        if (!Enum.TryParse<DebugTests.DutyModeSelection>(plan.Mode, out var mode))
        {
            Transition(plan, "RecoveryRequired", "Persisted duty mode is invalid.");
            return;
        }
        var report = eligibility(plan.TerritoryId, mode, plan.GearsetInspectionId);
        if (report.Status != "Eligible")
        {
            SetBlocker(plan, $"Fresh eligibility is {report.Status}; waiting without dispatch. {SummarizeEligibility(report)}");
            return;
        }
        if (plan.Blocker is not null)
            log.Information("Farming plan {PlanId} blocker cleared after fresh validation.", plan.PlanId);
        plan.Blocker = null;
        plan.RetryAfterUtc = null;
        var period = currentPeriodStart;
        var queuedRun = dungeonRun.Queue(new DungeonRunRequest(dungeon, mode,
            plan.GearsetInspectionId, plan.BareMode, plan.PlanId, period));
        plan.CurrentPeriodStartUtc = period;
        plan.Periods = [currentPeriod];
        plan.CurrentOperationId = queuedRun.OperationId;
        if (!SavePlan(plan, "record farming run operation"))
        {
            // Run is persisted PendingValidation but not yet dispatched (run controller ticks after this controller).
            dungeonRun.CancelPendingPlanRun(plan.PlanId);
            plan.CurrentOperationId = null;
            return;
        }
        log.Information("Farming plan {PlanId} period {PeriodStartUtc} queued run {OperationId}; period progress {Completed}/{Allocated}.",
            plan.PlanId, period, queuedRun.OperationId, currentPeriod.CompletedRuns, currentPeriod.AllocatedRuns);
    }

    private void Initialize(FarmingPlanRecord plan)
    {
        if (gameState.ContentId == 0 || gameState.ContentId != plan.CharacterContentId)
        {
            Transition(plan, "Stopped", "Start validation failed: farming character unavailable or changed.");
            return;
        }
        var now = Clock.UtcNow;
        var weekStart = plan.WeekStartUtc;
        var reset = plan.WeeklyResetUtc;
        if (FarmingSchedulePolicy.GetWeekStartUtc(now) != weekStart || now >= reset)
        {
            Transition(plan, "Expired", "The confirmed farming week has passed; plan will not start in a later week.");
            return;
        }
        var finishPeriods = plan.RequestedPeriodCount;
        if (finishPeriods is < 1 or > 7)
        {
            Transition(plan, "Stopped", "Start validation failed: period selection is invalid.");
            return;
        }
        var finish = plan.FinishDeadlineUtc;
        if (now >= finish)
        {
            Transition(plan, "DeadlineReached", "Selected finish boundary has already passed; no dungeon was launched.");
            return;
        }
        if (dungeonRun.Current is not null)
        {
            SetBlocker(plan, "An existing one-run operation must be safely acknowledged before farming can start.");
            return;
        }

        DebugTests.LiveTomeState tome;
        try { tome = readTomeState(); }
        catch (Exception error)
        {
            SetBlocker(plan, $"Start validation blocked; live tome state unknown: {error.GetBaseException().Message}");
            return;
        }
        if (tome.CharacterContentId != plan.CharacterContentId)
        {
            Transition(plan, "Stopped", "Start validation failed: character identity changed.");
            return;
        }
        var needed = FarmingSchedulePolicy.RunsNeeded(tome.WeeklyAcquired, tome.WeeklyLimit, plan.DungeonReward);
        plan.WeeklyAcquiredAtStart = tome.WeeklyAcquired;
        plan.WeeklyLimitAtStart = tome.WeeklyLimit;
        plan.CurrencyItemId = tome.CurrencyItemId;
        plan.CharacterContentId = tome.CharacterContentId;
        if (needed == 0)
        {
            plan.State = "Completed";
            plan.Detail = "Weekly tome cap already reached; no dungeon runs required.";
            SavePlan(plan, "complete already-capped plan");
            return;
        }
        if (!Enum.TryParse<DebugTests.DutyModeSelection>(plan.Mode, out var mode))
        {
            Transition(plan, "Stopped", "Start validation failed: invalid duty mode.");
            return;
        }
        var report = eligibility(plan.TerritoryId, mode, plan.GearsetInspectionId);
        if (report.Status != "Eligible")
        {
            SetBlocker(plan, $"Start validation {report.Status}; no run queued. {SummarizeEligibility(report)}");
            return;
        }
        plan.WeekStartUtc = weekStart;
        plan.WeeklyResetUtc = reset;
        plan.FinishDeadlineUtc = finish;
        plan.WeeklyAcquiredAtStart = tome.WeeklyAcquired;
        plan.WeeklyLimitAtStart = tome.WeeklyLimit;
        plan.CurrencyItemId = tome.CurrencyItemId;
        plan.CharacterContentId = tome.CharacterContentId;
        var periodStart = FarmingSchedulePolicy.GetPeriodStartUtc(now, weekStart);
        var target = FarmingSchedulePolicy.CurrentPeriodTarget(needed, periodStart, finish);
        plan.CurrentPeriodStartUtc = periodStart;
        plan.Periods = [new FarmingPeriodRecord { StartUtc = periodStart, AllocatedRuns = target }];
        plan.State = "Active";
        plan.Detail = target == 0
            ? "Weekly tome cap already reached; retainer and fishing scheduling remains active."
            : $"Current-period checklist created: 0/{target} runs.";
        plan.Blocker = null;
        if (!SavePlan(plan, "initialize farming plan"))
            Transition(plan, "RecoveryRequired", "Could not persist initialized farming plan.");
        else
            log.Information("Farming plan {PlanId} initialized; weekStart={WeekStartUtc}, deadline={DeadlineUtc}, currentPeriodTarget={Target}, weeklyRunsRemaining={RunsNeeded}, reward={Reward}, weekly={Weekly}/{Cap}, currencyItem={CurrencyItem}.",
                plan.PlanId, plan.WeekStartUtc, plan.FinishDeadlineUtc, target, needed,
                plan.DungeonReward, tome.WeeklyAcquired, tome.WeeklyLimit, plan.CurrencyItemId);
    }

    private void SetWaiting(FarmingPlanRecord plan, string detail)
    {
        plan.Blocker = null;
        plan.RetryAfterUtc = null;
        if (plan.Detail == detail)
            return;
        plan.Detail = detail;
        SavePlan(plan, "update scheduler status");
        log.Information("Farming plan {PlanId}: {Detail}", plan.PlanId, detail);
    }

    private void ObserveStoppedRun(FarmingPlanRecord plan)
    {
        if (dungeonRun.Current is not { State: "Completed", RewardOutcome: "Verified" } run ||
            run.FarmingPlanId != plan.PlanId || plan.CurrentOperationId != run.OperationId)
            return;
        if (!CreditVerifiedRun(plan, run))
            return;
        if (!dungeonRun.CanAcknowledge || !dungeonRun.Acknowledge())
            return;
        plan.CurrentOperationId = null;
        SavePlan(plan, "release observed stopped run");
    }

    private bool CreditVerifiedRun(FarmingPlanRecord plan, DungeonRunRecord run)
    {
        if (run.OperationId != plan.CurrentOperationId || run.FarmingPlanId != plan.PlanId ||
            run.FarmingPeriodStartUtc is not { } periodStart)
        {
            Transition(plan, "RecoveryRequired", "Verified run does not match persisted plan operation/period link.");
            return false;
        }
        if (!FarmingSchedulePolicy.ShouldCreditOperation(run.OperationId, plan.CreditedOperationIds))
        {
            plan.CompletedRetainerCheckpointOperationIds ??= [];
            if (plan.ServiceRetainers && !plan.StopRequested &&
                plan.RetainerCheckpointOperationId is null &&
                !plan.CompletedRetainerCheckpointOperationIds.Contains(run.OperationId, StringComparer.Ordinal))
            {
                plan.RetainerCheckpointOperationId = run.OperationId;
                if (!SavePlan(plan, "restore missing retainer checkpoint for credited run"))
                {
                    plan.RetainerCheckpointOperationId = null;
                    return false;
                }
            }
            return true;
        }
        var period = plan.Periods.FirstOrDefault(item => item.StartUtc == periodStart);
        if (period is null)
        {
            Transition(plan, "RecoveryRequired", "Verified run points to a period missing from persisted ledger.");
            return false;
        }
        period.CreditedOperationIds ??= [];
        plan.CreditedOperationIds.Add(run.OperationId);
        period.CreditedOperationIds.Add(run.OperationId);
        period.CompletedRuns++;
        var createRetainerCheckpoint = plan.ServiceRetainers && !plan.StopRequested;
        if (createRetainerCheckpoint)
            plan.RetainerCheckpointOperationId = run.OperationId;
        plan.Detail = $"Verified run credited to period {period.StartUtc:O} ({period.CompletedRuns}/{period.AllocatedRuns}).";
        if (!SavePlan(plan, "credit verified run"))
        {
            plan.CreditedOperationIds.Remove(run.OperationId);
            period.CreditedOperationIds.Remove(run.OperationId);
            period.CompletedRuns--;
            if (createRetainerCheckpoint)
                plan.RetainerCheckpointOperationId = null;
            return false;
        }
        log.Information("Farming plan {PlanId} credited verified run {OperationId} once; period={PeriodStartUtc}, progress={Completed}/{Allocated}.",
            plan.PlanId, run.OperationId, period.StartUtc, period.CompletedRuns, period.AllocatedRuns);
        return true;
    }

    private static RetainerAutomationSessionRecord CreateLinkedRetainerSession(
        FarmingPlanRecord plan, string name, string world, bool serviceRetainers) => new()
    {
        SessionId = plan.PlanId,
        FarmingPlanId = plan.PlanId,
        State = "Active",
        Detail = serviceRetainers
            ? "Retainer servicing is authorized as part of this confirmed farming plan."
            : "Retainer servicing is disabled for this confirmed farming plan.",
        FarmingCharacterContentId = plan.CharacterContentId,
        FarmingCharacterName = name,
        FarmingCharacterWorld = world,
        ServiceRetainers = serviceRetainers,
        CreatedUtc = plan.CreatedUtc,
        ExpiresUtc = plan.WeeklyResetUtc,
    };

    private RetainerAutomationSessionRecord ResumeLinkedRetainerSession(
        FarmingPlanRecord plan, string name, string world, bool serviceRetainers)
    {
        var session = configuration.RetainerSession;
        if (session is null || session.FarmingPlanId != plan.PlanId)
            return CreateLinkedRetainerSession(plan, name, world, serviceRetainers);
        session.State = "Active";
        session.StopRequested = false;
        session.Detail = "Combined farming plan explicitly resumed; prior pass/checkpoint ledger is preserved.";
        session.FarmingCharacterName = name;
        session.FarmingCharacterWorld = world;
        session.ServiceRetainers = serviceRetainers;
        return session;
    }

    private void SetBlocker(FarmingPlanRecord plan, string detail, bool persist = true)
    {
        if (plan.Blocker == detail)
            return;
        plan.Blocker = detail;
        plan.Detail = detail;
        plan.RetryAfterUtc = Clock.UtcNow.Add(BlockerRetry);
        log.Warning("Farming plan {PlanId} blocked; reevaluate after {RetryUtc}: {Blocker}",
            plan.PlanId, plan.RetryAfterUtc, detail);
        if (persist)
            SavePlan(plan, "update blocker");
    }

    private void Transition(FarmingPlanRecord plan, string state, string detail)
    {
        if (plan.State == state && plan.Detail == detail)
            return;
        var previous = plan.State;
        plan.State = state;
        if (state == "RecoveryRequired")
            plan.RecoveryKind ??= "Safety";
        plan.Detail = detail;
        log.Information("Farming plan {PlanId} state {PreviousState}->{State}: {Detail}", plan.PlanId, previous, state, detail);
        SavePlan(plan, $"state transition to {state}");
    }

    private static string SummarizeEligibility(DebugReport report)
    {
        var blockers = report.Text.Split('\n')
            .Where(line => line.Contains(": Blocked", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains(": Unknown", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        return blockers.Length == 0 ? "See Diagnostics for the detailed readiness report." : string.Join("\n", blockers);
    }

    private bool SavePlan(FarmingPlanRecord plan, string purpose)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception error)
        {
            plan.State = "RecoveryRequired";
            plan.RecoveryKind = "PersistenceFailure";
            plan.Detail = $"Persistence failed during {purpose}; automatic launches blocked.";
            log.Error(error, "Farming plan {PlanId} failed to persist {Purpose}; state set to RecoveryRequired.", plan.PlanId, purpose);
            return false;
        }
    }
}
