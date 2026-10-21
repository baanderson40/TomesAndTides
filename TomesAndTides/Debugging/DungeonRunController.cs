using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace TomesAndTides.Debugging;

/// <summary>Framework-thread controller for one explicitly confirmed AutoDuty run.</summary>
public sealed class DungeonRunController : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DutyTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan RewardTimeout = TimeSpan.FromSeconds(45);

    private readonly Configuration configuration;
    private readonly Action save;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly IPlayerState playerState;
    private readonly IDutyState dutyState;
    private readonly ICondition conditions;
    private readonly IPluginLog log;
    private readonly Func<uint, DebugTests.DutyModeSelection, int, DebugReport> eligibility;
    private readonly Func<DebugTests.LiveTomeState> readTomeState;
    private bool startEventSeen;
    private DateTimeOffset? dutyEnteredUtc;
    private DateTimeOffset? waitingForExitUtc;
    private DateTimeOffset? exitedUtc;
    private bool dispatchedThisRuntime;
    private bool canAcknowledgeSnapshot;
    private bool rewardReadFailureLogged;
    private int? lastLoggedRewardDelta;
    private int? lastPersistedRewardDelta;

    public DungeonRunController(Configuration configuration, Action save, IDalamudPluginInterface pluginInterface,
        IClientState client, IObjectTable objects, IPlayerState playerState, IDutyState dutyState,
        ICondition conditions, IPluginLog log,
        Func<uint, DebugTests.DutyModeSelection, int, DebugReport> eligibility,
        Func<DebugTests.LiveTomeState> readTomeState)
    {
        this.configuration = configuration;
        this.save = save;
        this.pluginInterface = pluginInterface;
        this.client = client;
        this.objects = objects;
        this.playerState = playerState;
        this.dutyState = dutyState;
        this.conditions = conditions;
        this.log = log;
        this.eligibility = eligibility;
        this.readTomeState = readTomeState;

        dutyState.DutyStarted += OnDutyStarted;
        dutyState.DutyCompleted += OnDutyCompleted;

        // A saved launch intent is never replayed. Reload makes its outcome uncertain.
        if (configuration.DungeonRun is { } savedRecord && string.IsNullOrWhiteSpace(savedRecord.OperationId))
            savedRecord.OperationId = Guid.NewGuid().ToString("N");
        if (configuration.DungeonRun is { } record && SingleRunPolicy.StateAfterReload(record.State) == "RecoveryRequired" &&
            record.State != "RecoveryRequired")
        {
            var previousState = record.State;
            record.State = "RecoveryRequired";
            record.Detail = "TT reloaded during the operation. No launch was repeated and AutoDuty was not stopped. Inspect duty/provider state before acknowledging.";
            log.Warning("Run {OperationId} recovered after reload from state {PreviousState}; no launch was repeated.",
                OperationId(record), previousState);
            Persist(record, "startup recovery", launchMayHaveHappened: true);
        }
    }

    public DungeonRunRecord? Current => configuration.DungeonRun;
    public bool HasUnacknowledgedRun => Current is { } run && run.State is not ("Completed" or "Failed");
    // Cached by Tick so ImGui rendering never performs game/IPC reads.
    public bool CanAcknowledge => canAcknowledgeSnapshot;

    /// <summary>Called by the confirmed UI action. Captures user choices; all IPC/native reads run on framework thread.</summary>
    public void Queue(DungeonRunRequest request)
    {
        if (configuration.DungeonRun is not null)
            throw new InvalidOperationException("An existing run record must be safely acknowledged first.");
        configuration.DungeonRun = new DungeonRunRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            TerritoryId = request.Dungeon.Territory,
            ContentFinderConditionId = request.Dungeon.ContentFinderConditionId,
            DungeonName = request.Dungeon.Name,
            Mode = request.Mode.ToString(),
            GearsetInspectionId = request.GearsetInspectionId,
            BareMode = request.BareMode,
            ExpectedReward = request.Dungeon.Reward,
            State = "PendingValidation",
            Detail = "Explicit confirmation recorded; awaiting final framework-thread validation.",
        };
        var run = configuration.DungeonRun!;
        try
        {
            save();
            log.Information("Run {OperationId} confirmed for {Dungeon} (Territory {Territory}, CFC {Cfc}, mode {Mode}, bareMode {BareMode}, gearset inspection {GearsetId}). Other integrations must not control AutoDuty until release.",
                OperationId(run), run.DungeonName, run.TerritoryId, run.ContentFinderConditionId,
                run.Mode, run.BareMode, run.GearsetInspectionId);
        }
        catch (Exception error)
        {
            configuration.DungeonRun = null;
            log.Error(error, "Run {OperationId} could not persist confirmation; AutoDuty was not called.", OperationId(run));
            throw;
        }
    }

    public void Tick()
    {
        try
        {
            var run = Current;
            if (run is null)
                return;

            switch (run.State)
            {
                case "PendingValidation":
                    if (SingleRunPolicy.MayDispatch(run.State))
                        DispatchOnce(run);
                    break;
                case "LaunchIntentRecorded":
                    // This state is only transient in one framework update. If observed later,
                    // provider outcome is ambiguous; never call Run again.
                    RequireRecovery(run, "Persisted launch intent has no confirmed provider response. Do not retry.");
                    break;
                case "AwaitingDuty":
                    ObserveEntry(run);
                    break;
                case "RunningDuty":
                    if (DateTimeOffset.UtcNow - (dutyEnteredUtc ?? run.RequestedUtc) > DutyTimeout)
                        RequireRecovery(run, "Duty observation timed out. TT did not stop AutoDuty, abandon, or retry.");
                    break;
                case "WaitingForExit":
                    ObserveExit(run);
                    break;
                case "WaitingForReward":
                    ObserveReward(run);
                    break;
            }
        }
        finally
        {
            canAcknowledgeSnapshot = Current is { State: "Completed" or "Failed" or "RecoveryRequired" } &&
                                     !dutyState.IsDutyStarted && !IsBoundByDuty() && IsAutoDutyStopped();
        }
    }

    public bool Acknowledge()
    {
        if (!CanAcknowledge)
            return false;
        var previous = configuration.DungeonRun;
        configuration.DungeonRun = null;
        try
        {
            save();
        }
        catch (Exception error)
        {
            configuration.DungeonRun = previous;
            log.Error(error, "Run {OperationId} acknowledgment could not be persisted; ownership record retained.",
                previous is null ? "unknown" : OperationId(previous));
            return false;
        }
        if (previous is not null)
            log.Information("Run {OperationId} acknowledged; ownership released. Final state {State}: {Detail}",
                OperationId(previous), previous.State, previous.Detail);
        startEventSeen = false;
        dutyEnteredUtc = null;
        exitedUtc = null;
        dispatchedThisRuntime = false;
        waitingForExitUtc = null;
        return true;
    }

    public void Dispose()
    {
        dutyState.DutyStarted -= OnDutyStarted;
        dutyState.DutyCompleted -= OnDutyCompleted;
    }

    private void DispatchOnce(DungeonRunRecord run)
    {
        if (!SingleRunPolicy.MayDispatch(run.State))
            return;
        // First framework update after confirmation snapshots character identity. No game/IPC
        // reads are performed from the ImGui draw path.
        if (run.CharacterContentId == 0)
        {
            run.CharacterContentId = playerState.ContentId;
            if (!Persist(run, "character snapshot", launchMayHaveHappened: false))
                return;
        }
        if (!IsSafeIdle(out var idleReason))
        {
            Fail(run, $"Final launch check failed; no AutoDuty call was made. {idleReason}");
            return;
        }
        if (playerState.ContentId == 0 || playerState.ContentId != run.CharacterContentId || !client.IsLoggedIn || objects.LocalPlayer is null)
        {
            Fail(run, "Character changed or is unavailable; no AutoDuty call was made.");
            return;
        }
        if (!Enum.TryParse<DebugTests.DutyModeSelection>(run.Mode, out var mode) ||
            eligibility(run.TerritoryId, mode, run.GearsetInspectionId).Status != "Eligible")
        {
            Fail(run, "Fresh eligibility did not return Eligible; no AutoDuty call was made.");
            return;
        }
        if (!TryReadConfig("Meta.AutoDutyModeEnum", out var executionMode) || executionMode != "Looping" ||
            !TryReadConfig("Meta.LoopTimes", out var loopCount) || loopCount != "1" ||
            !TryReadConfig("Meta.DutyModeEnum", out var configuredMode) ||
            !string.Equals(configuredMode, run.Mode, StringComparison.OrdinalIgnoreCase))
        {
            Fail(run, "AutoDuty must already be Looping, set to one configured loop, and match the selected mode. TT does not change its settings.");
            return;
        }
        if (!TryReadPath(run.TerritoryId, out var hasPath) || !hasPath)
        {
            Fail(run, "AutoDuty path is unavailable or unknown; no AutoDuty call was made.");
            return;
        }

        DebugTests.LiveTomeState tomeState;
        try
        {
            tomeState = readTomeState();
        }
        catch (Exception error)
        {
            Fail(run, $"Could not capture fresh weekly tome state; no AutoDuty call was made. {error.GetBaseException().Message}");
            return;
        }
        if (tomeState.CharacterContentId != playerState.ContentId || tomeState.WeeklyAcquired >= tomeState.WeeklyLimit)
        {
            Fail(run, "Character changed or weekly cap is reached; no AutoDuty call was made.");
            return;
        }
        var expectedPayout = Math.Min(run.ExpectedReward, tomeState.WeeklyLimit - tomeState.WeeklyAcquired);
        if (tomeState.HeldBalance > 2000 || 2000UL - tomeState.HeldBalance < (ulong)expectedPayout)
        {
            Fail(run, $"Held tome storage lacks room for expected cap-clamped payout {expectedPayout}; no AutoDuty call was made.");
            return;
        }

        run.CharacterContentId = playerState.ContentId;
        run.ExpectedReward = expectedPayout;
        run.WeeklyAcquiredBefore = tomeState.WeeklyAcquired;
        run.WeeklyLimitAtRequest = tomeState.WeeklyLimit;
        run.CurrencyItemId = tomeState.CurrencyItemId;
        run.RewardOutcome = "Pending";
        run.WeeklyResetUtcAtRequest = NextWeeklyResetUtc(DateTimeOffset.UtcNow);
        run.RequestedUtc = DateTimeOffset.UtcNow;
        log.Information("Run {OperationId} fresh launch validation passed; cappedCurrencyItem={CurrencyItem}, weekly={Weekly}/{Cap}, held={Held}/2000, expectedCapClampedPayout={Expected}.",
            OperationId(run), run.CurrencyItemId, tomeState.WeeklyAcquired, tomeState.WeeklyLimit,
            tomeState.HeldBalance, run.ExpectedReward);
        run.State = "LaunchIntentRecorded";
        run.Detail = "Launch intent persisted. AutoDuty will be called once; exceptions and timeouts are never retried.";
        log.Information("Run {OperationId} launch intent persisted; dispatching AutoDuty once (Territory {Territory}, CFC {Cfc}, mode {Mode}, bareMode {BareMode}).",
            OperationId(run), run.TerritoryId, run.ContentFinderConditionId, run.Mode, run.BareMode ?? true);
        if (!Persist(run, "launch intent", launchMayHaveHappened: false))
            return;

        try
        {
            // AutoDuty.Run(uint territory, int loops, bool bareMode) always sets execution mode
            // to Looping. Passing loops=0 avoids overwriting the already-verified configured count.
            // TT never calls SetConfig/PushConfigOverrides/PopConfigOverrides.
            pluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run")
                .InvokeAction(run.TerritoryId, 0, run.BareMode ?? true);
            dispatchedThisRuntime = true;
            run.State = "AwaitingDuty";
            run.Detail = $"AutoDuty.Run requested once for territory {run.TerritoryId}; bareMode={run.BareMode}. Waiting for matching duty entry.";
            log.Information("Run {OperationId} AutoDuty.Run IPC returned; this is not proof of queue acceptance. Waiting for duty entry.", OperationId(run));
            Persist(run, "AutoDuty.Run response", launchMayHaveHappened: true);
        }
        catch (Exception error)
        {
            RequireRecovery(run, $"AutoDuty.Run raised an exception; its outcome may be ambiguous. No retry: {error.GetBaseException().Message}", error);
        }
    }

    private void ObserveEntry(DungeonRunRecord run)
    {
        if (!dispatchedThisRuntime)
        {
            RequireRecovery(run, "Launch outcome is uncertain after reload; no retry.");
            return;
        }
        if (dutyState.IsDutyStarted || startEventSeen)
        {
            var currentCfc = dutyState.ContentFinderCondition.RowId;
            if (currentCfc == 0)
            {
                if (DateTimeOffset.UtcNow - run.RequestedUtc > StartTimeout)
                    RequireRecovery(run, "Duty entry was observed but its ContentFinderCondition is unavailable. No retry.");
                return;
            }
            if (currentCfc != run.ContentFinderConditionId)
            {
                RequireRecovery(run, $"Unexpected duty entry (CFC {currentCfc}); TT did not stop or retry AutoDuty.");
                return;
            }
            run.DutyStarted = true;
            run.State = "RunningDuty";
            run.Detail = "Matching duty entry observed. TT will not issue stop/leave commands.";
            dutyEnteredUtc = DateTimeOffset.UtcNow;
            log.Information("Run {OperationId} entered expected duty {Dungeon} (CFC {Cfc}, Territory {Territory}).",
                OperationId(run), run.DungeonName, run.ContentFinderConditionId, run.TerritoryId);
            Persist(run, "duty entry", launchMayHaveHappened: true);
            if (run.DutyCompleted)
                MoveToWaitingForExit(run);
            return;
        }
        if (DateTimeOffset.UtcNow - run.RequestedUtc > StartTimeout)
            RequireRecovery(run, "No matching duty entry observed before timeout. Inspect AutoDuty and duty state; TT will not retry.");
    }

    private void ObserveExit(DungeonRunRecord run)
    {
        if (DateTimeOffset.UtcNow - (waitingForExitUtc ?? DateTimeOffset.UtcNow) > StartTimeout)
        {
            RequireRecovery(run, "Duty completion was observed, but safe territory exit and AutoDuty stop were not both confirmed. TT did not leave or stop anything.");
            return;
        }
        if (dutyState.IsDutyStarted || IsBoundByDuty())
            return;
        if (client.TerritoryType == run.TerritoryId || dutyState.ContentFinderCondition.RowId == run.ContentFinderConditionId)
            return;
        if (!IsAutoDutyStopped())
            return;

        run.State = "WaitingForReward";
        run.Detail = "Duty completion, territory exit, and AutoDuty stopped state observed; waiting for tome count update.";
        exitedUtc = DateTimeOffset.UtcNow;
        log.Information("Run {OperationId} observed territory exit and AutoDuty stopped; awaiting weekly tome update.", OperationId(run));
        Persist(run, "duty exit", launchMayHaveHappened: true);
    }

    private void ObserveReward(DungeonRunRecord run)
    {
        var resetCrossed = run.WeeklyResetUtcAtRequest is { } reset && DateTimeOffset.UtcNow >= reset;
        var characterMatches = client.IsLoggedIn && playerState.ContentId != 0 && playerState.ContentId == run.CharacterContentId;
        var timedOut = SingleRunPolicy.TimedOut(DateTimeOffset.UtcNow, exitedUtc ?? DateTimeOffset.UtcNow, RewardTimeout);
        if (resetCrossed)
        {
            FinishUnverified(run, "Weekly reset boundary passed before reward reconciliation; weekly gain cannot be verified across reset.", "reset boundary");
            return;
        }
        if (!characterMatches)
        {
            FinishUnverified(run, "Character changed, logged out, or identity became unavailable during reward reconciliation.", "character identity");
            return;
        }

        DebugTests.LiveTomeState? current = null;
        try { current = readTomeState(); }
        catch (Exception error)
        {
            if (!rewardReadFailureLogged)
            {
                log.Warning(error, "Run {OperationId} live reward read failed; waiting for recovery until timeout.", OperationId(run));
                rewardReadFailureLogged = true;
            }
        }

        if (current is not null)
        {
            if (rewardReadFailureLogged)
            {
                log.Information("Run {OperationId} live reward read recovered.", OperationId(run));
                rewardReadFailureLogged = false;
            }
            if (current.CharacterContentId != run.CharacterContentId || current.CurrencyItemId != run.CurrencyItemId ||
                current.WeeklyLimit != run.WeeklyLimitAtRequest)
            {
                FinishUnverified(run, "Character, capped-currency identity, or live weekly cap changed during reward reconciliation.", "state identity mismatch");
                return;
            }

            var delta = current.WeeklyAcquired - run.WeeklyAcquiredBefore;
            run.WeeklyAcquiredAfter = current.WeeklyAcquired;
            run.ObservedWeeklyDelta = delta;
            if (lastLoggedRewardDelta != delta)
            {
                log.Information("Run {OperationId} reward observation changed; weekly={Before}->{After}, delta={Delta}, expected={Expected}.",
                    OperationId(run), run.WeeklyAcquiredBefore, current.WeeklyAcquired, delta, run.ExpectedReward);
                lastLoggedRewardDelta = delta;
            }
            if (lastPersistedRewardDelta != delta)
            {
                if (!Persist(run, "reward observation", launchMayHaveHappened: true))
                    return;
                lastPersistedRewardDelta = delta;
            }
            var outcome = SingleRunPolicy.ClassifyReward(run.ExpectedReward, delta, timedOut, false, true);
            if (outcome == SingleRunPolicy.RewardObservation.Verified)
            {
                run.State = "Completed";
                run.RewardOutcome = "Verified";
                run.Detail = $"Duty completed and exited; weekly count increased by expected cap-clamped payout {delta}. Concurrent tome gains cannot be excluded.";
                log.Information("Run {OperationId} reward verified; characterMatched=true, currencyItem={CurrencyItem}, weekly={Before}->{After}, delta={Delta}, expected={Expected}, cap={WeeklyCap}. Exclusive attribution is not established.",
                    OperationId(run), run.CurrencyItemId, run.WeeklyAcquiredBefore,
                    current.WeeklyAcquired, delta, run.ExpectedReward, run.WeeklyLimitAtRequest);
                Persist(run, "verified reward result", launchMayHaveHappened: true);
                return;
            }

            if (outcome == SingleRunPolicy.RewardObservation.Unverified)
            {
                FinishUnverified(run, $"Observed weekly delta {delta} does not match expected payout {run.ExpectedReward}; concurrent activity or reset may have affected count.", "delta mismatch");
                return;
            }
        }

        if (SingleRunPolicy.ClassifyReward(run.ExpectedReward, run.ObservedWeeklyDelta, timedOut,
                false, true) == SingleRunPolicy.RewardObservation.Unverified)
            FinishUnverified(run, $"Expected payout {run.ExpectedReward}; observed delta {run.ObservedWeeklyDelta?.ToString() ?? "unavailable"} after reward timeout.", "reward timeout");
    }

    private bool IsSafeIdle(out string reason)
    {
        if (!client.IsLoggedIn || objects.LocalPlayer is null)
            reason = "Character is not logged in.";
        else if (!IsAutoDutyStopped())
            reason = "AutoDuty is active or its state is unavailable.";
        else if (dutyState.IsDutyStarted || IsBoundByDuty())
            reason = "Character is in a duty.";
        else if (!IsAutoRetainerIdle(out var retainerReason))
            reason = retainerReason;
        else if (conditions[ConditionFlag.InCombat] || conditions[ConditionFlag.WaitingForDutyFinder] ||
                 conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51] ||
                 conditions[ConditionFlag.Occupied])
            reason = "Character is in combat, queued, transitioning, or occupied.";
        else
        {
            reason = string.Empty;
            return true;
        }
        return false;
    }

    private bool IsAutoRetainerIdle(out string reason)
    {
        try
        {
            var busy = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc();
            var multiMode = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.GetMultiModeStatus").InvokeFunc();
            reason = busy ? "AutoRetainer is busy." : multiMode ? "AutoRetainer Multi Mode is active." : string.Empty;
            return !busy && !multiMode;
        }
        catch (Exception error)
        {
            reason = $"AutoRetainer busy/Multi Mode state is unknown: {error.GetBaseException().Message}";
            return false;
        }
    }

    private bool IsBoundByDuty() => conditions[ConditionFlag.BoundByDuty] ||
                                    conditions[ConditionFlag.BoundByDuty56] ||
                                    conditions[ConditionFlag.BoundByDuty95];

    private bool IsAutoDutyStopped()
    {
        try { return pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped").InvokeFunc(); }
        catch { return false; }
    }

    private bool TryReadConfig(string name, out string value)
    {
        try
        {
            value = pluginInterface.GetIpcSubscriber<string, string>("AutoDuty.GetConfig").InvokeFunc(name);
            return !string.IsNullOrWhiteSpace(value);
        }
        catch { value = string.Empty; return false; }
    }

    private bool TryReadPath(uint territory, out bool path)
    {
        try { path = pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath").InvokeFunc(territory); return true; }
        catch { path = false; return false; }
    }

    private void FinishUnverified(DungeonRunRecord run, string detail, string reason)
    {
        run.State = "Completed";
        run.RewardOutcome = reason == "reset boundary" ? "ResetCrossed" : "Unverified";
        run.Detail = $"Duty completed and exited; reward outcome {run.RewardOutcome}. {detail}";
        log.Warning("Run {OperationId} reward outcome={RewardOutcome}, reason={Reason}, characterMatched={CharacterMatched}, currencyItem={CurrencyItem}, before={Before}, observed={After}, delta={Delta}, expected={Expected}, cap={WeeklyCap}. {Detail}",
            OperationId(run), run.RewardOutcome, client.IsLoggedIn && playerState.ContentId == run.CharacterContentId, run.CurrencyItemId,
            run.WeeklyAcquiredBefore, run.WeeklyAcquiredAfter?.ToString() ?? "unavailable",
            run.ObservedWeeklyDelta?.ToString() ?? "unavailable",
            run.ExpectedReward, run.WeeklyLimitAtRequest, detail);
        Persist(run, "unverified reward result", launchMayHaveHappened: true);
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        var run = Current;
        if (run is null || run.State is not ("AwaitingDuty" or "RunningDuty"))
            return;
        startEventSeen = true;
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        var run = Current;
        if (run is null || run.State is not ("AwaitingDuty" or "RunningDuty") ||
            dutyState.ContentFinderCondition.RowId != run.ContentFinderConditionId)
            return;
        run.DutyCompleted = true;
        log.Information("Run {OperationId} received duty-completed event for CFC {Cfc}.",
            OperationId(run), run.ContentFinderConditionId);
        if (run.State == "AwaitingDuty")
        {
            // Completion may arrive before the next framework poll observes duty entry.
            run.DutyStarted = true;
            dutyEnteredUtc = DateTimeOffset.UtcNow;
            MoveToWaitingForExit(run);
        }
        else if (run.State == "RunningDuty")
            MoveToWaitingForExit(run);
        else
            Persist(run, "early duty completion event", launchMayHaveHappened: true);
    }

    private void MoveToWaitingForExit(DungeonRunRecord run)
    {
        run.State = "WaitingForExit";
        run.Detail = "Duty-completed event observed. Waiting for duty-area exit and AutoDuty stopped; TT will not leave/stop anything.";
        waitingForExitUtc = DateTimeOffset.UtcNow;
        Persist(run, "duty completion", launchMayHaveHappened: true);
    }

    private static DateTimeOffset NextWeeklyResetUtc(DateTimeOffset now)
    {
        // FFXIV weekly reset: Tuesday 08:00 UTC (subject to maintenance/game changes).
        var days = ((int)DayOfWeek.Tuesday - (int)now.DayOfWeek + 7) % 7;
        var reset = new DateTimeOffset(now.Year, now.Month, now.Day, 8, 0, 0, TimeSpan.Zero).AddDays(days);
        return reset <= now ? reset.AddDays(7) : reset;
    }

    private void Fail(DungeonRunRecord run, string detail)
    {
        run.State = "Failed";
        run.Detail = detail;
        log.Warning("Run {OperationId} blocked before launch: {Detail}", OperationId(run), detail);
        Persist(run, "failed validation", launchMayHaveHappened: false);
    }

    private void RequireRecovery(DungeonRunRecord run, string detail, Exception? error = null)
    {
        run.State = "RecoveryRequired";
        run.Detail = detail;
        if (error is not null)
            log.Error(error, "Run {OperationId} requires recovery; outcome is uncertain and no retry will occur. {Detail}", OperationId(run), detail);
        else
            log.Warning("Run {OperationId} requires recovery; no retry/stop/leave action will occur. {Detail}", OperationId(run), detail);
        Persist(run, "recovery state", launchMayHaveHappened: true);
    }

    private bool Persist(DungeonRunRecord run, string purpose, bool launchMayHaveHappened)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception error)
        {
            run.State = launchMayHaveHappened ? "RecoveryRequired" : "Failed";
            run.Detail = $"Could not persist {purpose}; " +
                         (launchMayHaveHappened ? "provider outcome must be inspected before acknowledgment." : "AutoDuty was not called.");
            log.Error(error, "Run {OperationId} failed to persist {Purpose}; resulting state {State}.",
                OperationId(run), purpose, run.State);
            return false;
        }
    }

    private static string OperationId(DungeonRunRecord run) =>
        string.IsNullOrWhiteSpace(run.OperationId) ? "unknown" : run.OperationId;
}

public sealed record DungeonRunRequest(DebugTests.DungeonOption Dungeon, DebugTests.DutyModeSelection Mode,
    int GearsetInspectionId, bool BareMode);
