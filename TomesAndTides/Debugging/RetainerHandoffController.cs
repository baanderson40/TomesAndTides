using TomesAndTides.Integrations;

namespace TomesAndTides.Debugging;

/// <summary>Framework-thread, persisted coordinator for explicitly authorized AutoRetainer passes.</summary>
public sealed class RetainerHandoffController(
    Configuration configuration,
    Action save,
    IRetainerProvider autoRetainer,
    ILifestreamProvider lifestream,
    IWorkflowGameState gameState,
    DungeonRunController dungeonRun,
    FarmingPlanController farmingPlan,
    Func<RetainerReadinessSnapshot?> readReadiness,
    Func<RetainerReadinessSnapshot> refreshReadiness,
    Func<bool?> readAutoDutyStopped,
    IWorkflowLog log,
    IUtcClock? clock = null) : IDisposable
{
    private static readonly TimeSpan ProviderPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StableIdleTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PassTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PassCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadinessFreshness = TimeSpan.FromSeconds(30);
    private int completionEventPending;
    private DateTimeOffset nextProviderPollUtc;
    private DateTimeOffset nextPreflightUtc;
    private DateTimeOffset nextCheckpointScanUtc;
    private DateTimeOffset? idleSinceUtc;
    private DateTimeOffset? safeSinceUtc;
    private bool? providerBusy;
    private bool? providerActive;
    private bool? lifestreamBusy;
    private IUtcClock Clock { get; } = clock ?? SystemUtcClock.Instance;

    public RetainerAutomationSessionRecord? Session => configuration.RetainerSession;
    public RetainerHandoffRecord? Handoff => configuration.RetainerHandoff;
    public bool OwnsActivity => Handoff is { State: not ("Completed" or "RecoveryAcknowledged") };
    public bool NeedsRecovery => Session is { State: "RecoveryRequired" } || Handoff is { State: "RecoveryRequired" };

    public event Action? StateChanged;

    public void Subscribe() => autoRetainer.SinglePassFinished += OnSinglePassFinished;

    public void Dispose() => autoRetainer.SinglePassFinished -= OnSinglePassFinished;

    public void RecoverAfterReload()
    {
        var session = Session;
        var handoff = Handoff;
        if (session is not null && handoff is { } completed && farmingPlan.Current is { } completedPlan &&
            completedPlan.PlanId == session.FarmingPlanId &&
            RetainerHandoffPolicy.MayReconcileCompletedCheckpoint(completed.State,
                completed.CompletionEventObserved, completed.SessionId, session.SessionId,
                completed.FarmingOperationId, completedPlan.RetainerCheckpointOperationId) &&
            completed.FarmingOperationId is { } completedOperationId)
        {
            if (farmingPlan.CompleteRetainerCheckpoint(completedOperationId,
                    "Reconciled persisted completed retainer handoff after reload; no provider request replayed."))
                log.Information("Retainer handoff {HandoffId} checkpoint {OperationId} reconciled after reload without replay.",
                    completed.HandoffId, completedOperationId);
            else
                RequireRecovery("Persisted completed handoff checkpoint could not be reconciled after reload.");
            return;
        }
        var activeSession = session is { State: "Active" or "StopRequested" };
        var activeHandoff = handoff is { State: not ("Completed" or "RecoveryAcknowledged") };
        if (!activeSession && !activeHandoff)
            return;
        if (session is not null)
        {
            session.State = "RecoveryRequired";
            session.Detail = "Plugin reloaded during a retainer automation session. Inspect provider and character state; no request will be replayed.";
        }
        if (handoff is not null && activeHandoff)
        {
            handoff.State = "RecoveryRequired";
            handoff.RecoveryKind = "Reload";
            handoff.Detail = "Plugin reloaded during an owned handoff. AutoRetainer and Lifestream intents will not be replayed.";
            handoff.PhaseChangedUtc = Clock.UtcNow;
        }
        SaveOrBlock("reload recovery");
        log.Warning("Retainer session {SessionId} requires explicit reload recovery; no provider action will be replayed.", session?.SessionId ?? "unknown");
    }

    public void RequestStop()
    {
        var session = Session;
        if (session is not { State: "Active" })
            return;
        farmingPlan.RequestStop(preserveOwnedRetainerCheckpoint: OwnsActivity);
        if (OwnsActivity)
        {
            session.StopRequested = true;
            session.State = "StopRequested";
            session.Detail = "Stop requested; the current owned activity will settle safely, with no new dungeon or retainer work.";
        }
        else
        {
            session.State = "Stopped";
            session.StopRequested = true;
            session.Detail = "Automation session stopped at a safe boundary; no further dungeon or retainer work will start.";
        }
        SaveOrBlock("stop retainer session");
        log.Information("Retainer session {SessionId} Stop requested; handoffOwned={HandoffOwned}.", session.SessionId, OwnsActivity);
        StateChanged?.Invoke();
    }

    public bool AcknowledgeRecoveryAndStop()
    {
        if (!NeedsRecovery || !IsFarmingCharacterCurrent() || !IsSafePlayerState() || !TryPollProviders() ||
            providerBusy != false || providerActive != false || lifestreamBusy != false || readAutoDutyStopped() != true)
            return false;
        if (Handoff is { } handoff)
        {
            handoff.State = "RecoveryAcknowledged";
            handoff.RecoveryKind = null;
            handoff.Detail = "User acknowledged recovery after verifying the farming character and all providers idle.";
            handoff.PhaseChangedUtc = Clock.UtcNow;
        }
        if (Session is { } session)
        {
            session.State = "Stopped";
            session.StopRequested = true;
            session.Detail = "Recovery acknowledged and retainer session stopped. Start a new session explicitly to resume.";
        }
        farmingPlan.RequestStop();
        if (!SaveOrBlock("acknowledge retainer recovery"))
            return false;
        log.Warning("Retainer handoff recovery acknowledged and session stopped; no ambiguous request was replayed.");
        StateChanged?.Invoke();
        return true;
    }

    public void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception error)
        {
            RequireRecovery($"Unexpected handoff coordinator error: {error.GetBaseException().Message}", error);
        }
    }

    private void TickCore()
    {
        var session = Session;
        var plan = farmingPlan.Current;
        if (session is null || plan is null || session.FarmingPlanId != plan.PlanId ||
            session.State is not ("Active" or "StopRequested"))
            return;
        if (OwnsActivity)
        {
            if (Handoff is not { } ownedHandoff ||
                !RetainerHandoffPolicy.HandoffMatchesCheckpoint(ownedHandoff.SessionId, session.SessionId,
                    ownedHandoff.FarmingOperationId, plan.RetainerCheckpointOperationId))
            {
                RequireRecovery("Owned retainer handoff does not match the active plan/session checkpoint; continuation is blocked.");
                return;
            }
            TickHandoff(session, Handoff!);
            return;
        }
        if (session.StopRequested)
        {
            SetSessionState(session, "Stopped", "Stop completed at safe boundary; no new retainer pass will be started.");
            return;
        }
        if (plan.StopRequested || plan.State is "PendingValidation" or "RecoveryRequired" or "Stopped" or "Expired")
            return;
        var now = Clock.UtcNow;
        if (now >= session.ExpiresUtc)
        {
            SetSessionState(session, "Expired", "Weekly reset reached; no new retainer passes will start until a new explicit session.");
            return;
        }
        var checkpointOperationId = plan.RetainerCheckpointOperationId;
        var checkpointPending = checkpointOperationId is not null;
        if (!checkpointPending && !farmingPlan.AllowsRetainerHandoffNow)
            return;
        var serviceEnabled = plan.ServiceRetainers && configuration.MonitorRetainerVentures;
        if (checkpointPending && !serviceEnabled)
        {
            CompleteCheckpoint(plan, checkpointOperationId!, "Retainer checkpoint skipped because venture service is disabled; farming may continue.");
            return;
        }
        var readiness = readReadiness();
        if (checkpointPending && (readiness is null || now - readiness.CapturedUtc > ReadinessFreshness))
        {
            if (now >= nextCheckpointScanUtc)
            {
                nextCheckpointScanUtc = now.AddSeconds(5);
                readiness = refreshReadiness();
            }
            if (readiness is null || now - readiness.CapturedUtc > ReadinessFreshness)
                return;
        }
        if (checkpointPending && serviceEnabled && readiness is { Known: true, Ready: false })
        {
            if (RetainerHandoffPolicy.ShouldWaitForNearCompleteVenture(readiness.Known, readiness.Ready,
                    readiness.ClosestEligibleSeconds, plan.RetainerWaitBeforeDungeonMinutes))
                return;
            if (!TryPollProviders() || providerBusy != false || providerActive != false || lifestreamBusy != false ||
                readAutoDutyStopped() != true)
                return;
            CompleteCheckpoint(plan, checkpointOperationId!, "Retainer checkpoint complete; no ready venture is within the configured wait window.");
            return;
        }
        var readinessFresh = readiness is not null && now - readiness.CapturedUtc <= ReadinessFreshness;
        var cooldownElapsed = session.LastPassUtc is not { } lastPass || now - lastPass >= PassCooldown;
        if (!RetainerHandoffPolicy.MayRequestPass(
                session.State == "Active", session.StopRequested, serviceEnabled,
                now < session.ExpiresUtc, checkpointPending || farmingPlan.AllowsRetainerHandoffNow,
                dungeonRun.Current is not null, OwnsActivity,
                readiness?.Known == true, readiness?.Ready == true, readinessFresh, cooldownElapsed))
            return;
        if (now < nextPreflightUtc)
            return;
        nextPreflightUtc = now.AddSeconds(5);
        if (!Preflight(session, out var freshReadiness))
            return;
        if (RetainerHandoffPolicy.IsUnchangedReadyWork(session.LastPassReadinessFingerprint, freshReadiness.Fingerprint))
        {
            session.State = "RecoveryRequired";
            session.Detail = "The same selected venture work remains ready after its pass. Review AutoRetainer; dungeon continuation is paused.";
            SaveOrBlock("pause unchanged retainer work for review");
            log.Warning("Combined farming session {SessionId} paused for review; selected ready venture work did not change after its prior pass.", session.SessionId);
            StateChanged?.Invoke();
            return;
        }
        BeginPass(session, freshReadiness);
    }

    private bool Preflight(RetainerAutomationSessionRecord session, out RetainerReadinessSnapshot readiness)
    {
        readiness = refreshReadiness();
        var plan = farmingPlan.Current;
        if (session.State != "Active" || session.StopRequested || plan is null ||
            plan.PlanId != session.FarmingPlanId || plan.StopRequested || !plan.ServiceRetainers ||
            !configuration.MonitorRetainerVentures ||
            plan.RetainerCheckpointOperationId is null && !farmingPlan.AllowsRetainerHandoffNow)
            return false;
        if (!IsFarmingCharacterCurrent() || !IsSafePlayerState() || dungeonRun.Current is not null)
            return false;
        var autoDuty = readAutoDutyStopped();
        if (autoDuty != true || !TryPollProviders() || providerBusy != false || providerActive != false || lifestreamBusy != false)
            return false;
        if (!readiness.Known || !readiness.Ready || Clock.UtcNow - readiness.CapturedUtc > ReadinessFreshness)
            return false;
        log.Information("Retainer handoff preflight passed for session {SessionId}; readyCharacters={ReadyCharacters}.",
            session.SessionId, readiness.ReadyCharacters);
        return true;
    }

    private void BeginPass(RetainerAutomationSessionRecord session, RetainerReadinessSnapshot readiness)
    {
        if (!TryGetCharacter(out var contentId, out var name, out var world) || contentId != session.FarmingCharacterContentId ||
            name != session.FarmingCharacterName || world != session.FarmingCharacterWorld)
            return;
        var now = Clock.UtcNow;
        var handoff = new RetainerHandoffRecord
        {
            HandoffId = Guid.NewGuid().ToString("N"),
            SessionId = session.SessionId,
            FarmingCharacterContentId = contentId,
            FarmingCharacterName = name,
            FarmingCharacterWorld = world,
            State = "LaunchIntentRecorded",
            Detail = "Retainers-only single-pass launch intent recorded; AutoRetainer will be requested exactly once.",
            CreatedUtc = now,
            PhaseChangedUtc = now,
            LaunchIntentUtc = now,
            ReadinessFingerprintAtLaunch = readiness.Fingerprint,
            FarmingOperationId = farmingPlan.Current?.RetainerCheckpointOperationId,
        };
        configuration.RetainerHandoff = handoff;
        if (!SaveOrBlock("record AutoRetainer launch intent"))
        {
            handoff.State = "RecoveryRequired";
            handoff.RecoveryKind = "PersistenceFailure";
            handoff.Detail = "Could not persist launch intent; provider was not called. Inspect and acknowledge recovery.";
            return;
        }
        Interlocked.Exchange(ref completionEventPending, 0);
        log.Information("Retainer handoff {HandoffId} launch intent persisted; requesting one Retainers-only AutoRetainer pass.", handoff.HandoffId);
        try
        {
            autoRetainer.RequestSingleRetainerPass();
            handoff.State = "AwaitingStart";
            handoff.Detail = "AutoRetainer request returned; waiting for owned pass lifecycle evidence.";
            handoff.PhaseChangedUtc = Clock.UtcNow;
            if (!SaveOrBlock("record AutoRetainer request response"))
            {
                RequireRecovery("AutoRetainer may have accepted the request, but the response state could not be persisted.");
                return;
            }
            log.Information("Retainer handoff {HandoffId} AutoRetainer request returned; acceptance is not yet verified.", handoff.HandoffId);
        }
        catch (Exception error)
        {
            RequireRecovery($"AutoRetainer request outcome is ambiguous; it will not be retried: {error.GetBaseException().Message}", error);
        }
    }

    private void TickHandoff(RetainerAutomationSessionRecord session, RetainerHandoffRecord handoff)
    {
        var now = Clock.UtcNow;
        var eventObserved = Interlocked.Exchange(ref completionEventPending, 0) != 0;
        if (eventObserved)
        {
            handoff.CompletionEventObserved = true;
            log.Information("Retainer handoff {HandoffId} observed the AutoRetainer single-pass completion event.", handoff.HandoffId);
            if (!PersistHandoff(handoff, "completion event"))
                return;
        }
        if (now >= nextProviderPollUtc)
        {
            nextProviderPollUtc = now.Add(ProviderPollInterval);
            if (!TryPollProviders())
            {
                if (handoff.LaunchIntentUtc is not null)
                    RequireRecovery("AutoRetainer/Lifestream state became unavailable after a possible provider request.");
                return;
            }
        }

        switch (handoff.State)
        {
            case "LaunchIntentRecorded":
                RequireRecovery("Reloaded after AutoRetainer launch intent; request outcome is ambiguous and will not be replayed.");
                break;
            case "AwaitingStart":
                if (providerBusy == true || providerActive == true)
                {
                    handoff.SawProviderActive = true;
                    SetHandoffState(handoff, "RunningPass", "Owned AutoRetainer activity observed; waiting for completion.");
                }
                else if (handoff.CompletionEventObserved)
                    SetHandoffState(handoff, "WaitingForProviderIdle", "Completion event observed; confirming stable provider idle.");
                else if (now - handoff.PhaseChangedUtc >= StartTimeout)
                    RequireRecovery("AutoRetainer did not show owned activity before timeout; request will not be repeated.");
                break;
            case "RunningPass":
                if (handoff.CompletionEventObserved)
                    SetHandoffState(handoff, "WaitingForProviderIdle", "Completion event observed; confirming stable provider idle.");
                else if (handoff.SawProviderActive && providerBusy == false && providerActive == false)
                    RequireRecovery("AutoRetainer became idle without the owned completion event; pass outcome is uncertain.");
                else if (now - handoff.CreatedUtc >= PassTimeout)
                    RequireRecovery("Owned AutoRetainer pass exceeded two hours; provider was not stopped and request was not repeated.");
                break;
            case "WaitingForProviderIdle":
                if (providerBusy == false && providerActive == false)
                {
                    idleSinceUtc ??= now;
                    if (now - idleSinceUtc.Value >= StableIdleTime)
                    {
                        idleSinceUtc = null;
                        if (IsFarmingCharacterCurrent())
                            SetHandoffState(handoff, "VerifyingReturn", "Pass settled on the farming character; verifying stable safe state.");
                        else
                            RecordReturnIntent(session, handoff);
                    }
                }
                else
                    idleSinceUtc = null;
                break;
            case "ReturnIntentRecorded":
                TryIssueReturn(session, handoff);
                break;
            case "AwaitingFarmingCharacter":
                if (IsFarmingCharacterCurrent())
                    SetHandoffState(handoff, "VerifyingReturn", "Farming character login observed; verifying identity and safe state.");
                else if (handoff.ReturnIntentUtc is { } returnAt && now - returnAt >= ReturnTimeout)
                    RequireRecovery("Farming-character return timed out; no second Lifestream request will be sent.");
                break;
            case "VerifyingReturn":
                if (RetainerHandoffPolicy.ReturnVerificationTimedOut(now, handoff.PhaseChangedUtc, ReturnTimeout))
                {
                    RequireRecovery("Return identity/safe-state verification timed out; no second Lifestream request will be sent.");
                    break;
                }
                var farmingCharacterMatched = IsFarmingCharacterCurrent();
                var playerSafe = IsSafePlayerState();
                var autoDutyStopped = readAutoDutyStopped() == true;
                var providerIdle = providerBusy == false && providerActive == false;
                var lifestreamIdle = lifestreamBusy == false;
                if (!farmingCharacterMatched || !playerSafe || !providerIdle || !lifestreamIdle || !autoDutyStopped)
                    safeSinceUtc = null;
                else
                    safeSinceUtc ??= now;
                var stableIdle = safeSinceUtc is { } safeSince && now - safeSince >= StableIdleTime;
                if (RetainerHandoffPolicy.MayReleaseOwnership(handoff.CompletionEventObserved, providerIdle,
                        stableIdle, farmingCharacterMatched, playerSafe, autoDutyStopped, lifestreamIdle))
                    CompleteHandoff(session, handoff);
                break;
            case "RecoveryRequired":
                break;
        }
    }

    private void RecordReturnIntent(RetainerAutomationSessionRecord session, RetainerHandoffRecord handoff)
    {
        handoff.ReturnIntentUtc = Clock.UtcNow;
        handoff.State = "ReturnIntentRecorded";
        handoff.PhaseChangedUtc = Clock.UtcNow;
        handoff.Detail = "Return intent persisted; Lifestream will be requested once after fresh idle checks.";
        if (!PersistHandoff(handoff, "record farming-character return intent"))
            return;
        TryIssueReturn(session, handoff);
    }

    private void TryIssueReturn(RetainerAutomationSessionRecord session, RetainerHandoffRecord handoff)
    {
        if (IsFarmingCharacterCurrent())
        {
            SetHandoffState(handoff, "VerifyingReturn", "Farming character already present; verifying stable safe state.");
            return;
        }
        if (handoff.ReturnIntentUtc is { } intentAt && Clock.UtcNow - intentAt >= ReturnTimeout)
        {
            RequireRecovery("Return preflight did not become safe before timeout; no Lifestream request was issued.");
            return;
        }
        if (providerBusy != false || providerActive != false || lifestreamBusy != false ||
            readAutoDutyStopped() != true || !IsSafePlayerState())
            return;
        log.Information("Retainer handoff {HandoffId} return intent persisted; requesting farming-character return once.", handoff.HandoffId);
        if (!lifestream.TryChangeCharacter(session.FarmingCharacterName, session.FarmingCharacterWorld, out var result))
        {
            RequireRecovery($"Lifestream rejected or ambiguously failed the one-time return request: {result}");
            return;
        }
        handoff.State = "AwaitingFarmingCharacter";
        handoff.Detail = "Lifestream accepted the one-time return request; verifying farming-character identity.";
        handoff.PhaseChangedUtc = Clock.UtcNow;
        if (!PersistHandoff(handoff, "record Lifestream response"))
            RequireRecovery("Lifestream may have accepted the return request, but its response could not be persisted.");
    }

    private void CompleteHandoff(RetainerAutomationSessionRecord session, RetainerHandoffRecord handoff)
    {
        var now = Clock.UtcNow;
        handoff.State = "Completed";
        handoff.Detail = "Retainers-only pass completed; farming character returned and verified safe. Ownership released.";
        handoff.PhaseChangedUtc = now;
        session.LastPassUtc = now;
        session.LastPassReadinessFingerprint = handoff.ReadinessFingerprintAtLaunch;
        if (session.StopRequested)
        {
            session.State = "Stopped";
            session.Detail = "Stop completed after owned pass and farming-character return cleanup.";
        }
        else
            session.Detail = "Retainer pass completed and farming character verified; session remains active.";
        if (SaveOrBlock("complete retainer handoff"))
        {
            log.Information("Retainer handoff {HandoffId} completed and credited once; session {SessionId} is {State}.",
                handoff.HandoffId, session.SessionId, session.State);
            if (farmingPlan.Current is { RetainerCheckpointOperationId: { } operationId } plan)
            {
                if (handoff.FarmingOperationId != operationId)
                    RequireRecovery("Completed retainer handoff does not match the persisted dungeon checkpoint; continuation is blocked.");
                else
                    CompleteCheckpoint(plan, operationId,
                        "Retainer pass completed, farming character verified, and linked dungeon checkpoint cleared.");
            }
        }
        StateChanged?.Invoke();
    }

    private void CompleteCheckpoint(FarmingPlanRecord plan, string operationId, string detail)
    {
        if (!farmingPlan.CompleteRetainerCheckpoint(operationId, detail))
            RequireRecovery("Retainer checkpoint could not be durably completed; dungeon continuation is blocked.");
        else
            log.Information("Farming plan {PlanId} completed retainer checkpoint for dungeon operation {OperationId}.",
                plan.PlanId, operationId);
    }

    private bool TryPollProviders()
    {
        var arKnown = autoRetainer.TryGetProviderState(out var busy, out var active);
        var lsKnown = lifestream.TryGetBusy(out var lsBusy);
        providerBusy = arKnown ? busy : null;
        providerActive = arKnown ? active : null;
        lifestreamBusy = lsKnown ? lsBusy : null;
        return arKnown && lsKnown;
    }

    private bool TryGetCharacter(out ulong contentId, out string name, out string world)
    {
        contentId = gameState.ContentId;
        name = string.Empty;
        world = string.Empty;
        if (!gameState.IsLoggedIn || contentId == 0 || !gameState.HasLocalPlayer)
            return false;
        name = gameState.CharacterName;
        world = gameState.HomeWorld;
        return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(world);
    }

    private bool IsFarmingCharacterCurrent() => TryGetCharacter(out var contentId, out var name, out var world) &&
        Session is { } session && contentId == session.FarmingCharacterContentId &&
        string.Equals(name, session.FarmingCharacterName, StringComparison.Ordinal) &&
        string.Equals(world, session.FarmingCharacterWorld, StringComparison.Ordinal);

    private bool IsSafePlayerState() => gameState.IsSafePlayerState;

    private void SetHandoffState(RetainerHandoffRecord handoff, string state, string detail)
    {
        if (handoff.State == state)
            return;
        var previous = handoff.State;
        handoff.State = state;
        handoff.Detail = detail;
        handoff.PhaseChangedUtc = Clock.UtcNow;
        idleSinceUtc = null;
        safeSinceUtc = null;
        if (PersistHandoff(handoff, "handoff phase transition"))
        {
            log.Information("Retainer handoff {HandoffId} state {Previous}->{Current}: {Detail}", handoff.HandoffId, previous, state, detail);
            StateChanged?.Invoke();
        }
    }

    private void RequireRecovery(string detail, Exception? error = null)
    {
        if (Handoff is not { } handoff)
            return;
        if (handoff.State != "RecoveryRequired")
        {
            handoff.State = "RecoveryRequired";
            handoff.RecoveryKind ??= "AmbiguousProviderOutcome";
            handoff.Detail = detail;
            handoff.PhaseChangedUtc = Clock.UtcNow;
            if (Session is { } session)
            {
                session.State = "RecoveryRequired";
                session.Detail = detail;
            }
            SaveOrBlock("retainer recovery transition");
            if (error is null)
                log.Error("Retainer handoff {HandoffId} requires recovery: {Detail}", handoff.HandoffId, detail);
            else
                log.Error(error, "Retainer handoff {HandoffId} requires recovery: {Detail}", handoff.HandoffId, detail);
            StateChanged?.Invoke();
        }
    }

    private void SetSessionState(RetainerAutomationSessionRecord session, string state, string detail)
    {
        session.State = state;
        session.Detail = detail;
        SaveOrBlock("retainer session state");
        log.Information("Retainer session {SessionId} state changed to {State}: {Detail}", session.SessionId, state, detail);
        StateChanged?.Invoke();
    }

    private bool PersistHandoff(RetainerHandoffRecord handoff, string context) => SaveOrBlock(context);

    private bool SaveOrBlock(string context)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception error)
        {
            log.Error(error, "Could not persist {Context}; retainer handoff is blocked.", context);
            if (Handoff is { } handoff && handoff.State != "RecoveryRequired")
            {
                handoff.State = "RecoveryRequired";
                handoff.RecoveryKind = "PersistenceFailure";
                handoff.Detail = $"Persistence failed during {context}; provider work is paused for explicit recovery.";
                handoff.PhaseChangedUtc = Clock.UtcNow;
                if (Session is { } session)
                {
                    session.State = "RecoveryRequired";
                    session.Detail = handoff.Detail;
                }
            }
            return false;
        }
    }

    private void OnSinglePassFinished() => Interlocked.Exchange(ref completionEventPending, 1);
}
