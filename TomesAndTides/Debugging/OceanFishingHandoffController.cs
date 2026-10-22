using TomesAndTides.Integrations;

namespace TomesAndTides.Debugging;

/// <summary>Owns one persisted WahTools voyage claim per eligible Ocean Fishing window.</summary>
public sealed class OceanFishingHandoffController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StableCleanup = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableReturn = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CharacterTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BoardingTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan VoyageTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StatusFreshness = TimeSpan.FromSeconds(5);
    private const uint OceanTerritoryOne = 900;
    private const uint OceanTerritoryTwo = 1163;

    private readonly Configuration configuration;
    private readonly Action save;
    private readonly IWorkflowGameState gameState;
    private readonly IRetainerProvider autoRetainer;
    private readonly Func<bool> retainerOperationOwned;
    private readonly ILifestreamProvider lifestream;
    private readonly IWahToolsFeeshClient feesh;
    private readonly Func<bool> dungeonOperationOwned;
    private readonly Func<bool?> readAutoDutyStopped;
    private readonly IWorkflowLog log;
    private readonly IUtcClock clock;
    private DateTimeOffset? cleanupStableSinceUtc;
    private DateTimeOffset? returnStableSinceUtc;
    private DateTimeOffset nextProviderPollUtc;
    private bool sawOceanTerritory;
    private bool disposed;
    private OceanFishingAdmissionDiagnostic? admission;
    private DateTimeOffset? observedEligibleWindowStartUtc;
    private IReadOnlyList<RetainerCharacterIdentity> fishingRoster = [];
    private bool fishingRosterAvailable;
    private DateTimeOffset nextFishingRosterReadUtc;
    public bool CanResumeAutomationSession { get; private set; }

    public OceanFishingHandoffController(Configuration configuration, Action save, IWorkflowGameState gameState,
        IRetainerProvider autoRetainer, ILifestreamProvider lifestream, IWahToolsFeeshClient feesh,
        Func<bool> dungeonOperationOwned, Func<bool?> readAutoDutyStopped, IWorkflowLog log, IUtcClock? clock = null,
        Func<bool>? retainerOperationOwned = null)
    {
        this.configuration = configuration;
        this.save = save;
        this.gameState = gameState;
        this.autoRetainer = autoRetainer;
        this.retainerOperationOwned = retainerOperationOwned ?? (() => false);
        this.lifestream = lifestream;
        this.feesh = feesh;
        this.dungeonOperationOwned = dungeonOperationOwned;
        this.readAutoDutyStopped = readAutoDutyStopped;
        this.log = log;
        this.clock = clock ?? SystemUtcClock.Instance;
        gameState.DutyStarted += OnDutyStarted;
        gameState.DutyCompleted += OnDutyCompleted;
    }

    public OceanFishingOperationRecord? Current => configuration.OceanFishingOperation;
    public OceanFishingAdmissionDiagnostic? Admission => admission;
    public bool OwnsActivity => Current is { State: not ("Completed" or "Skipped" or "RecoveryAcknowledged") };
    public bool NeedsRecovery => Current is { State: "RecoveryRequired" };
    public bool PrioritizesNow
    {
        get
        {
            if (OwnsActivity)
                return true;
            if (configuration.AutomationSession is not { State: "Active", OceanFishingEnabled: true } session)
                return false;
            var window = OceanFishingSchedulePolicy.GetWindow(clock.UtcNow, session.OceanFishingPreparationLeadMinutes);
            return OceanFishingSchedulePolicy.IsPreparationEligible(clock.UtcNow, window) &&
                   !OceanFishingSchedulePolicy.IsWindowHandled(configuration.HandledOceanFishingWindowsUtc, window.StartUtc);
        }
    }
    public string Status => Current?.Detail ?? admission?.Detail ?? "Monitoring Ocean Fishing windows";

    /// <summary>Refreshes cached recovery preflight on the framework thread.</summary>
    public void RefreshResumeReadiness()
    {
        CanResumeAutomationSession = false;
        if (configuration.AutomationSession is not { State: "RecoveryRequired", RecoveryKind: "Reload" } session ||
            Current is { State: not ("Completed" or "Skipped" or "RecoveryAcknowledged") } ||
            !TryCurrentCharacter(out var cid, out var name, out var world) || cid != session.FarmingCharacterContentId ||
            name != session.FarmingCharacterName || world != session.FarmingCharacterWorld ||
            !gameState.IsSafePlayerState || IsOceanTerritory(gameState.TerritoryType) || readAutoDutyStopped() != true ||
            !autoRetainer.TryGetProviderState(out var retainerBusy, out var retainerActive) || retainerBusy || retainerActive ||
            !lifestream.TryGetBusy(out var lifestreamBusy) || lifestreamBusy)
            return;
        if (!session.OceanFishingEnabled)
        {
            CanResumeAutomationSession = true;
            return;
        }
        var status = feesh.ReadStatus();
        CanResumeAutomationSession = status.IsClean && clock.UtcNow - status.ObservedUtc <= StatusFreshness;
    }

    public void RecoverAfterReload()
    {
        if (Current is not { State: not ("Completed" or "Skipped" or "RecoveryAcknowledged") } operation)
            return;
        operation.State = "RecoveryRequired";
        operation.RecoveryKind = "Reload";
        operation.Detail = "Plugin reloaded during an Ocean Fishing handoff. Inspect Feesh and character state; no provider request will be replayed.";
        operation.PhaseChangedUtc = clock.UtcNow;
        SetSessionRecovery(operation, "Ocean Fishing handoff requires explicit recovery after reload.");
        SaveOrRecover("Ocean Fishing reload recovery");
    }

    public bool AcknowledgeRecoveryAndStop()
    {
        if (!NeedsRecovery || Current is not { } operation || !MatchesFarmingCharacter(operation) ||
            !gameState.IsSafePlayerState || IsOceanTerritory(gameState.TerritoryType) || readAutoDutyStopped() != true ||
            !autoRetainer.TryGetProviderState(out var retainerBusy, out var retainerActive) || retainerBusy || retainerActive ||
            !lifestream.TryGetBusy(out var lifestreamBusy) || lifestreamBusy)
            return false;
        var status = feesh.ReadStatus();
        if (!status.IsClean || clock.UtcNow - status.ObservedUtc > StatusFreshness)
            return false;
        operation.State = "RecoveryAcknowledged";
        operation.RecoveryKind = null;
        operation.Detail = "User verified farming character and all providers idle; ambiguous voyage was not replayed.";
        operation.PhaseChangedUtc = clock.UtcNow;
        configuration.HandledOceanFishingWindowsUtc ??= [];
        if (!configuration.HandledOceanFishingWindowsUtc.Contains(operation.WindowStartUtc))
            configuration.HandledOceanFishingWindowsUtc.Add(operation.WindowStartUtc);
        if (configuration.AutomationSession is { } session && session.SessionId == operation.SessionId)
        {
            session.State = "Stopped";
            session.StopRequested = true;
            session.RecoveryKind = null;
            session.Detail = "Recovery acknowledged and automation stopped. Start a new session explicitly to resume.";
        }
        SaveOrRecover("acknowledge Ocean Fishing recovery");
        return !NeedsRecovery;
    }

    public void Tick(bool allowNewWindow = true)
    {
        if (disposed)
            return;
        try
        {
            if (Current is { } operation && OwnsActivity)
            {
                TickOperation(operation);
                return;
            }
            if (!TryAdmitNewWindow(allowNewWindow, out var session, out var window, out var target, out _))
                return;

            var claimedOperation = new OceanFishingOperationRecord
            {
                OperationId = Guid.NewGuid().ToString("N"),
                SessionId = session.SessionId,
                WindowStartUtc = window.StartUtc,
                FarmingCharacterContentId = session.FarmingCharacterContentId,
                FarmingCharacterName = session.FarmingCharacterName,
                FarmingCharacterWorld = session.FarmingCharacterWorld,
                FishingCharacterContentId = target.ContentId,
                FishingCharacterName = target.Name,
                FishingCharacterWorld = target.World,
                State = "WindowClaimed",
                Detail = $"Claimed Ocean Fishing window {window.StartUtc:O}; one voyage maximum.",
                PhaseChangedUtc = clock.UtcNow,
            };
            configuration.OceanFishingOperation = claimedOperation;
            if (!SaveOrRecover("Ocean Fishing window claim"))
                return;
            log.Information("Ocean Fishing operation {OperationId} claimed window {WindowStartUtc} for selected character {Name}@{World}.",
                claimedOperation.OperationId, claimedOperation.WindowStartUtc, claimedOperation.FishingCharacterName, claimedOperation.FishingCharacterWorld);
            BeginTravelOrPrepare(claimedOperation);
        }
        catch (Exception error)
        {
            var detail = $"Unexpected Ocean Fishing preflight/coordinator error: {error.GetBaseException().Message}";
            if (Current is null)
                PublishDiagnostic("CoordinatorError", detail);
            else
                RequireRecovery(detail, error);
        }
    }

    public void Dispose()
    {
        disposed = true;
        gameState.DutyStarted -= OnDutyStarted;
        gameState.DutyCompleted -= OnDutyCompleted;
    }

    private bool TryAdmitNewWindow(bool allowNewWindow, out AutomationSessionRecord session,
        out OceanFishingWindow window, out RetainerCharacterIdentity target, out WahToolsFeeshStatus feeshStatus)
    {
        session = configuration.AutomationSession!;
        window = default;
        target = new RetainerCharacterIdentity(0, string.Empty, string.Empty);
        feeshStatus = WahToolsFeeshStatus.Unknown("Not read");
        var now = clock.UtcNow;
        if (session is not { State: "Active", StopRequested: false, OceanFishingEnabled: true })
        {
            observedEligibleWindowStartUtc = null;
            PublishDiagnostic("SessionInactive", "Ocean Fishing is not enabled in an active automation session.");
            return false;
        }
        window = OceanFishingSchedulePolicy.GetWindow(now, session.OceanFishingPreparationLeadMinutes);
        if (observedEligibleWindowStartUtc is { } observedWindow && now >= observedWindow.Add(OceanFishingSchedulePolicy.LaunchCutoffOffset))
        {
            observedEligibleWindowStartUtc = null;
            var missedCutoff = observedWindow.Add(OceanFishingSchedulePolicy.LaunchCutoffOffset);
            if (!OceanFishingSchedulePolicy.IsWindowHandled(configuration.HandledOceanFishingWindowsUtc, observedWindow))
            {
                MarkWindowHandled(observedWindow);
                PublishDiagnostic("MissedLaunchCutoff",
                    $"Observed Ocean Fishing window {observedWindow:HH:mm} UTC passed its launch cutoff; no retry will occur.",
                    observedWindow, missedCutoff);
                return false;
            }
        }
        if (OceanFishingSchedulePolicy.IsPreparationEligible(now, window) &&
            !OceanFishingSchedulePolicy.IsWindowHandled(configuration.HandledOceanFishingWindowsUtc, window.StartUtc))
            observedEligibleWindowStartUtc = window.StartUtc;
        if (!allowNewWindow)
        {
            PublishDiagnostic("AdmissionPaused", "Another owned activity or recovery has paused new Ocean Fishing admission.", window.StartUtc, window.LaunchCutoffUtc);
            return false;
        }
        if (OceanFishingSchedulePolicy.IsWindowHandled(configuration.HandledOceanFishingWindowsUtc, window.StartUtc))
        {
            PublishDiagnostic("WindowAlreadyHandled", $"Ocean Fishing window {window.StartUtc:O} was already consumed.", window.StartUtc, window.LaunchCutoffUtc);
            return false;
        }
        if (now < window.PreparationStartUtc || now >= window.LaunchCutoffUtc)
        {
            PublishDiagnostic("OutsidePreparationWindow",
                $"Waiting for preparation at {window.PreparationStartUtc:HH:mm} UTC for window {window.StartUtc:HH:mm} UTC; launch cutoff is {window.LaunchCutoffUtc:HH:mm} UTC.",
                window.StartUtc, window.LaunchCutoffUtc);
            return false;
        }
        if (dungeonOperationOwned())
        {
            PublishDiagnostic("DungeonOwnsActivity", "Waiting for the owned dungeon operation to settle before fishing admission.", window.StartUtc, window.LaunchCutoffUtc);
            return false;
        }
        if (retainerOperationOwned())
        {
            PublishDiagnostic("RetainerOwnsActivity", "Waiting for the owned AutoRetainer pass to settle; it will not be interrupted.",
                window.StartUtc, window.LaunchCutoffUtc, autoRetainerBusy: true);
            return false;
        }
        if (!gameState.IsLoggedIn || !gameState.HasLocalPlayer)
        {
            PublishDiagnostic("PlayerUnavailable", "Logged-in player identity is unavailable.", window.StartUtc, window.LaunchCutoffUtc, false);
            return false;
        }
        if (!gameState.IsSafePlayerState)
        {
            PublishDiagnostic("PlayerUnsafe", "Waiting for a safe player state before Ocean Fishing.", window.StartUtc, window.LaunchCutoffUtc, false);
            return false;
        }
        var autoDutyStopped = readAutoDutyStopped();
        if (autoDutyStopped != true)
        {
            PublishDiagnostic(autoDutyStopped is null ? "AutoDutyUnknown" : "AutoDutyRunning",
                autoDutyStopped is null ? "AutoDuty stopped state is unknown." : "AutoDuty must be stopped before fishing admission.",
                window.StartUtc, window.LaunchCutoffUtc, true, autoDutyStopped);
            return false;
        }
        if (!autoRetainer.TryGetProviderState(out var retainerBusy, out var retainerActive))
        {
            PublishDiagnostic("AutoRetainerUnknown", "AutoRetainer state IPC is unavailable; fishing admission remains blocked.",
                window.StartUtc, window.LaunchCutoffUtc, true, true, null, null);
            return false;
        }
        if (retainerBusy || retainerActive)
        {
            PublishDiagnostic("AutoRetainerBusy", retainerBusy ? "AutoRetainer is busy; its owned work will not be preempted." : "AutoRetainer Multi Mode is active.",
                window.StartUtc, window.LaunchCutoffUtc, true, true, retainerBusy, retainerActive);
            return false;
        }
        if (!lifestream.TryGetBusy(out var lifestreamBusy))
        {
            PublishDiagnostic("LifestreamUnknown", "Lifestream busy state is unavailable; fishing admission remains blocked.",
                window.StartUtc, window.LaunchCutoffUtc, true, true, false, false, null);
            return false;
        }
        if (lifestreamBusy)
        {
            PublishDiagnostic("LifestreamBusy", "Lifestream is busy; waiting before fishing admission.",
                window.StartUtc, window.LaunchCutoffUtc, true, true, false, false, true);
            return false;
        }
        feeshStatus = feesh.ReadStatus();
        if (!feeshStatus.Available)
        {
            PublishDiagnostic("WahToolsUnavailable", feeshStatus.Detail, window.StartUtc, window.LaunchCutoffUtc,
                true, true, false, false, false, false, null, feeshStatus.Detail);
            return false;
        }
        if (!feeshStatus.IsClean)
        {
            var (code, detail) = DescribeFeeshBlocker(feeshStatus);
            PublishDiagnostic(code, detail, window.StartUtc, window.LaunchCutoffUtc,
                true, true, false, false, false, true, false, feeshStatus.Detail);
            return false;
        }
        if (now - feeshStatus.ObservedUtc > StatusFreshness)
        {
            PublishDiagnostic("WahToolsStatusStale", $"WahTools status is stale ({(now - feeshStatus.ObservedUtc).TotalSeconds:F1}s old).",
                window.StartUtc, window.LaunchCutoffUtc, true, true, false, false, false, true, false, feeshStatus.Detail);
            return false;
        }
        if (!TrySelectFishingCharacter(session, out target))
        {
            var reason = session.OceanFishingCharacterMode == "SelectedCharacter"
                ? fishingRosterAvailable ? "Selected fishing character is absent or no longer matches the registered AutoRetainer roster."
                    : "AutoRetainer character roster is unavailable."
                : "Current farming-character identity is invalid.";
            PublishDiagnostic("FishingCharacterUnavailable", reason, window.StartUtc, window.LaunchCutoffUtc,
                true, true, false, false, false, true, true, feeshStatus.Detail);
            return false;
        }
        PublishDiagnostic("AdmissionReady", $"Preflight passed for Ocean Fishing window {window.StartUtc:HH:mm} UTC; one voyage may launch before {window.LaunchCutoffUtc:HH:mm} UTC.",
            window.StartUtc, window.LaunchCutoffUtc, true, true, false, false, false, true, true, feeshStatus.Detail, information: true);
        return true;
    }

    private bool TryCheckLaunchReadiness(OceanFishingOperationRecord operation, out WahToolsFeeshStatus status)
    {
        var cutoff = operation.WindowStartUtc.Add(OceanFishingSchedulePolicy.LaunchCutoffOffset);
        status = WahToolsFeeshStatus.Unknown("Not read");
        if (!MatchesFishingCharacter(operation))
        {
            PublishDiagnostic("FishingCharacterNotCurrent", "Waiting for the selected fishing-character identity.", operation.WindowStartUtc, cutoff);
            return false;
        }
        if (!gameState.IsSafePlayerState)
        {
            PublishDiagnostic("PlayerUnsafe", "Waiting for a safe fishing-character state before launch.", operation.WindowStartUtc, cutoff, false);
            return false;
        }
        var autoDutyStopped = readAutoDutyStopped();
        if (autoDutyStopped != true)
        {
            PublishDiagnostic(autoDutyStopped is null ? "AutoDutyUnknown" : "AutoDutyRunning",
                autoDutyStopped is null ? "AutoDuty stopped state is unknown." : "AutoDuty must be stopped before launch.",
                operation.WindowStartUtc, cutoff, true, autoDutyStopped);
            return false;
        }
        if (!autoRetainer.TryGetProviderState(out var busy, out var active) || busy || active)
        {
            PublishDiagnostic(busy || active ? "AutoRetainerBusy" : "AutoRetainerUnknown",
                busy ? "AutoRetainer is busy." : active ? "AutoRetainer Multi Mode is active." : "AutoRetainer state is unknown.",
                operation.WindowStartUtc, cutoff, true, true, busy, active);
            return false;
        }
        if (!lifestream.TryGetBusy(out var lifestreamBusy) || lifestreamBusy)
        {
            PublishDiagnostic(lifestreamBusy ? "LifestreamBusy" : "LifestreamUnknown",
                lifestreamBusy ? "Lifestream is busy." : "Lifestream busy state is unknown.",
                operation.WindowStartUtc, cutoff, true, true, false, false, lifestreamBusy);
            return false;
        }
        status = feesh.ReadStatus();
        if (!status.Available || !status.IsClean || clock.UtcNow - status.ObservedUtc > StatusFreshness)
        {
            var (code, detail) = !status.Available
                ? ("WahToolsUnavailable", status.Detail)
                : !status.IsClean
                    ? DescribeFeeshBlocker(status)
                    : ("WahToolsStatusStale", "WahTools Feesh status is stale.");
            PublishDiagnostic(code, detail, operation.WindowStartUtc, cutoff, true, true, false, false, false,
                status.Available, status.IsClean, status.Detail);
            return false;
        }
        return true;
    }

    private static (string Code, string Detail) DescribeFeeshBlocker(WahToolsFeeshStatus status)
    {
        if (status.TripRunning is null)
            return ("FeeshTripStateUnknown", "WahTools trip running state is unknown.");
        if (status.TripRunning == true)
            return ("FeeshTripRunning", $"WahTools trip is running ({status.TripState ?? "state unavailable"}; {status.TripStatus ?? "status unavailable"}).");
        if (status.OnBoat is null)
            return ("FeeshBoatStateUnknown", "WahTools boat state is unknown.");
        if (status.OnBoat == true)
            return ("FeeshOnBoat", "WahTools reports the player is already on the boat.");
        if (status.Cleaning is null || status.CleanerRunning is null || status.DesyntherRunning is null ||
            status.DiscarderRunning is null || status.BuyerBusy is null)
            return ("FeeshCleanupStateUnknown",
                $"WahTools cleanup state incomplete: Cleaning={Show(status.Cleaning)}, Cleaner={Show(status.CleanerRunning)}, Desynther={Show(status.DesyntherRunning)}, Discarder={Show(status.DiscarderRunning)}, Buyer={Show(status.BuyerBusy)}.");
        return ("FeeshCleanupBusy",
            $"WahTools cleanup still active: Cleaning={status.Cleaning}, Cleaner={status.CleanerRunning}, Desynther={status.DesyntherRunning}, Discarder={status.DiscarderRunning}, Buyer={status.BuyerBusy}.");
    }

    private static string Show(bool? value) => value?.ToString() ?? "unknown";

    private void PublishDiagnostic(string code, string detail, DateTimeOffset? windowStart = null,
        DateTimeOffset? cutoff = null, bool? playerSafe = null, bool? autoDutyStopped = null,
        bool? autoRetainerBusy = null, bool? autoRetainerMultiMode = null, bool? lifestreamBusy = null,
        bool? wahToolsAvailable = null, bool? feeshClean = null, string? feeshDetail = null, bool information = false)
    {
        var next = new OceanFishingAdmissionDiagnostic(clock.UtcNow, windowStart, cutoff, code, detail,
            playerSafe, autoDutyStopped, autoRetainerBusy, autoRetainerMultiMode, lifestreamBusy,
            wahToolsAvailable, feeshClean, feeshDetail);
        if (admission is { } previous && previous with { ObservedUtc = next.ObservedUtc } == next)
            return;
        admission = next;
        if (information || IsExpectedWaitCode(code))
            log.Information("Ocean Fishing admission {Code}: {Detail} window={WindowStartUtc} cutoff={CutoffUtc}.",
                code, detail, windowStart?.ToString("O") ?? "unknown", cutoff?.ToString("O") ?? "unknown");
        else
            log.Warning("Ocean Fishing admission blocked {Code}: {Detail} window={WindowStartUtc} cutoff={CutoffUtc}.",
                code, detail, windowStart?.ToString("O") ?? "unknown", cutoff?.ToString("O") ?? "unknown");
    }

    private static bool IsExpectedWaitCode(string code) => code is
        "SessionInactive" or "AdmissionPaused" or "OutsidePreparationWindow" or "WindowAlreadyHandled" or
        "DungeonOwnsActivity" or "RetainerOwnsActivity" or "PlayerUnsafe" or "PlayerUnavailable";

    private void TickOperation(OceanFishingOperationRecord operation)
    {
        var now = clock.UtcNow;
        if (operation.State == "RecoveryRequired")
            return;
        if (configuration.AutomationSession is { StopRequested: true } && operation.LaunchIntentUtc is null)
        {
            BeginReturn(operation, "Manual Stop before voyage launch; safely returning without starting WahTools.", skipped: true);
            return;
        }
        if (now >= nextProviderPollUtc)
        {
            nextProviderPollUtc = now.Add(PollInterval);
            var status = feesh.ReadStatus();
            if (!status.Available && operation.LaunchIntentUtc is not null)
            {
                RequireRecovery("WahTools Feesh state became unavailable during an owned voyage/cleanup.");
                return;
            }
        }

        switch (operation.State)
        {
            case "WindowClaimed":
                BeginTravelOrPrepare(operation);
                break;
            case "AwaitingFishingCharacter":
                if (MatchesFishingCharacter(operation))
                    SetState(operation, "VerifyingFishingCharacter", "Selected fishing character observed; verifying safe state.");
                else if (operation.OutboundIntentUtc is { } outbound && now - outbound >= CharacterTimeout)
                    RequireRecovery("Selected fishing character did not become available before timeout; no second Lifestream request will be sent.");
                break;
            case "VerifyingFishingCharacter":
                if (MatchesFishingCharacter(operation) && gameState.IsSafePlayerState)
                    PrepareAndLaunch(operation);
                else if (now - operation.PhaseChangedUtc >= CharacterTimeout)
                    RequireRecovery("Fishing character safety verification timed out.");
                break;
            case "LaunchIntentRecorded":
                RequireRecovery("Reload/phase ambiguity after WahTools launch intent; CatchTheBoat will not be repeated.");
                break;
            case "AwaitingBoarding":
                ObserveBoarding(operation, now);
                break;
            case "VoyageRunning":
                if (now - operation.LaunchIntentUtc.GetValueOrDefault() >= VoyageTimeout)
                    RequireRecovery("Ocean voyage exceeded two hours; TT will not stop or relaunch WahTools.");
                else if (operation.DutyCompleted)
                    SetState(operation, "WaitingForDutyExit", "Matching Ocean Fishing completion observed; waiting for safe duty exit.");
                break;
            case "WaitingForDutyExit":
                if (gameState.IsDutyStarted || gameState.IsBoundByDuty)
                    break;
                if (IsOceanTerritory(gameState.TerritoryType))
                    break;
                if (!operation.DutyCompleted)
                {
                    RequireRecovery("Ocean territory exited without a matching duty-completion event; voyage result is unconfirmed.");
                    break;
                }
                SetState(operation, "WaitingForProviderCleanup", "Voyage exit verified; waiting for stable WahTools cleanup.");
                break;
            case "WaitingForProviderCleanup":
                if (now - operation.PhaseChangedUtc >= CleanupTimeout)
                {
                    RequireRecovery("WahTools cleanup did not reach a verified idle state before timeout.");
                    break;
                }
                WaitForCleanupThenReturn(operation, now);
                break;
            case "ReturnIntentRecorded":
                IssueReturn(operation);
                break;
            case "AwaitingFarmingCharacter":
                if (MatchesFarmingCharacter(operation))
                    SetState(operation, "VerifyingReturn", "Farming character observed after voyage; verifying safe stable return.");
                else if (operation.ReturnIntentUtc is { } returned && now - returned >= ReturnTimeout)
                    RequireRecovery("Farming-character return timed out; no second Lifestream request will be issued.");
                break;
            case "VerifyingReturn":
                VerifyReturn(operation, now);
                break;
        }
    }

    private void BeginTravelOrPrepare(OceanFishingOperationRecord operation)
    {
        if (!TryCurrentCharacter(out var cid, out var name, out var world))
            return;
        if (cid == operation.FishingCharacterContentId && name == operation.FishingCharacterName && world == operation.FishingCharacterWorld)
        {
            SetState(operation, "VerifyingFishingCharacter", "Fishing character already logged in; verifying safe state.");
            return;
        }
        operation.OutboundIntentUtc = clock.UtcNow;
        operation.State = "OutboundIntentRecorded";
        operation.PhaseChangedUtc = clock.UtcNow;
        operation.Detail = "Outbound Lifestream intent persisted; requesting selected fishing character once.";
        if (!SaveOrRecover("Ocean Fishing outbound intent"))
            return;
        if (!lifestream.TryChangeCharacter(operation.FishingCharacterName, operation.FishingCharacterWorld, out var result))
        {
            RequireRecovery($"Lifestream outbound request was refused or ambiguous: {result}");
            return;
        }
        SetState(operation, "AwaitingFishingCharacter", "Lifestream accepted outbound switch; verifying selected identity.");
    }

    private void PrepareAndLaunch(OceanFishingOperationRecord operation)
    {
        var window = OceanFishingSchedulePolicy.GetWindow(clock.UtcNow,
            configuration.AutomationSession?.OceanFishingPreparationLeadMinutes ?? 15);
        if (window.StartUtc != operation.WindowStartUtc || clock.UtcNow >= window.LaunchCutoffUtc)
        {
            BeginReturn(operation, "Missed safe launch cutoff; skipping this Ocean Fishing window.", skipped: true);
            return;
        }
        if (!TryCheckLaunchReadiness(operation, out var feeshStatus))
            return;
        PublishDiagnostic("LaunchReady", "All launch preflight checks passed; recording the one-time WahTools launch intent.",
            operation.WindowStartUtc, window.LaunchCutoffUtc, true, true, false, false, false,
            feeshStatus.Available, feeshStatus.IsClean, feeshStatus.Detail, information: true);

        operation.LaunchIntentUtc = clock.UtcNow;
        operation.State = "LaunchIntentRecorded";
        operation.PhaseChangedUtc = clock.UtcNow;
        operation.Detail = "WahTools single-voyage launch intent persisted; CatchTheBoat will be requested once.";
        if (!SaveOrRecover("WahTools launch intent"))
            return;
        var request = feesh.RequestSingleVoyage(out var detail);
        if (request == FeeshRequestResult.Accepted)
        {
            operation.State = "AwaitingBoarding";
            operation.PhaseChangedUtc = clock.UtcNow;
            operation.Detail = detail;
            if (!SaveOrRecover("WahTools launch response"))
                RequireRecovery("WahTools may have accepted launch but its response was not persisted.");
        }
        else if (request == FeeshRequestResult.Refused)
            BeginReturn(operation, detail, skipped: true);
        else
        {
            operation.LaunchAmbiguous = true;
            RequireRecovery(detail);
        }
    }

    private void ObserveBoarding(OceanFishingOperationRecord operation, DateTimeOffset now)
    {
        if (IsOceanTerritory(gameState.TerritoryType))
        {
            operation.OceanTerritoryEntered = true;
            sawOceanTerritory = true;
        }
        if (gameState.IsDutyStarted || gameState.IsBoundByDuty)
        {
            operation.DutyStarted = true;
            SetState(operation, "VoyageRunning", "Ocean Fishing duty observed for the claimed voyage.");
            return;
        }
        if (operation.OceanTerritoryEntered)
            return;
        if (now - operation.PhaseChangedUtc >= BoardingTimeout)
            RequireRecovery("WahTools launch was accepted but no matching Ocean Fishing boarding was observed; no relaunch will be attempted.");
    }

    private void WaitForCleanupThenReturn(OceanFishingOperationRecord operation, DateTimeOffset now)
    {
        var status = feesh.ReadStatus();
        var providersIdle = autoRetainer.TryGetProviderState(out var busy, out var active) && !busy && !active &&
                            lifestream.TryGetBusy(out var lifestreamBusy) && !lifestreamBusy && readAutoDutyStopped() == true;
        var safe = !gameState.IsDutyStarted && !gameState.IsBoundByDuty && !IsOceanTerritory(gameState.TerritoryType);
        if (!status.IsClean || now - status.ObservedUtc > StatusFreshness || !providersIdle || !safe)
        {
            cleanupStableSinceUtc = null;
            return;
        }
        cleanupStableSinceUtc ??= now;
        if (now - cleanupStableSinceUtc.Value < StableCleanup)
            return;
        cleanupStableSinceUtc = null;
        BeginReturn(operation, "Voyage and provider cleanup verified; returning to farming character.", skipped: false);
    }

    private void BeginReturn(OceanFishingOperationRecord operation, string detail, bool skipped)
    {
        operation.Detail = detail;
        if (skipped)
            operation.RecoveryKind = "Skipped";
        if (MatchesFarmingCharacter(operation))
        {
            SetState(operation, "VerifyingReturn", detail);
            return;
        }
        operation.ReturnIntentUtc = clock.UtcNow;
        operation.State = "ReturnIntentRecorded";
        operation.PhaseChangedUtc = clock.UtcNow;
        if (!SaveOrRecover("Ocean Fishing return intent"))
            return;
        IssueReturn(operation);
    }

    private void IssueReturn(OceanFishingOperationRecord operation)
    {
        if (MatchesFarmingCharacter(operation))
        {
            SetState(operation, "VerifyingReturn", "Farming character already present; verifying safe return.");
            return;
        }
        if (operation.ReturnIntentUtc is { } intent && clock.UtcNow - intent >= ReturnTimeout)
        {
            RequireRecovery("Return preflight timed out; no Lifestream request was issued.");
            return;
        }
        if (!gameState.IsSafePlayerState || IsOceanTerritory(gameState.TerritoryType) || readAutoDutyStopped() != true ||
            !lifestream.TryGetBusy(out var busy) || busy || !feesh.ReadStatus().IsClean)
            return;
        if (!lifestream.TryChangeCharacter(operation.FarmingCharacterName, operation.FarmingCharacterWorld, out var result))
        {
            RequireRecovery($"Lifestream return request failed or ambiguous: {result}");
            return;
        }
        operation.State = "AwaitingFarmingCharacter";
        operation.Detail = "Lifestream accepted the one-time farming-character return; identity verification pending.";
        operation.PhaseChangedUtc = clock.UtcNow;
        if (!SaveOrRecover("Lifestream return response"))
            RequireRecovery("Lifestream may have accepted return but response persistence failed.");
    }

    private void VerifyReturn(OceanFishingOperationRecord operation, DateTimeOffset now)
    {
        if (now - operation.PhaseChangedUtc >= ReturnTimeout)
        {
            RequireRecovery("Farming-character safe-state verification timed out; return will not be repeated.");
            return;
        }
        var safe = MatchesFarmingCharacter(operation) && gameState.IsSafePlayerState && !IsOceanTerritory(gameState.TerritoryType) &&
                   readAutoDutyStopped() == true && lifestream.TryGetBusy(out var busy) && !busy &&
                   feesh.ReadStatus() is { IsClean: true } status && now - status.ObservedUtc <= StatusFreshness &&
                   autoRetainer.TryGetProviderState(out var arBusy, out var active) && !arBusy && !active;
        if (!safe)
        {
            returnStableSinceUtc = null;
            return;
        }
        returnStableSinceUtc ??= now;
        if (now - returnStableSinceUtc.Value < StableReturn)
            return;
        var skipped = operation.RecoveryKind == "Skipped";
        SetState(operation, skipped ? "Skipped" : "Completed",
            skipped ? "Window safely skipped; farming character returned." : "Voyage completed, WahTools cleanup settled, and farming character returned safely.");
        MarkWindowHandled(operation.WindowStartUtc);
    }

    private bool TrySelectFishingCharacter(AutomationSessionRecord session, out RetainerCharacterIdentity target)
    {
        target = new RetainerCharacterIdentity(0, string.Empty, string.Empty);
        if (session.OceanFishingCharacterMode == "CurrentCharacter")
        {
            target = new RetainerCharacterIdentity(session.FarmingCharacterContentId,
                session.FarmingCharacterName, session.FarmingCharacterWorld);
            return target.ContentId != 0 && !string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(target.World);
        }
        if (session.OceanFishingCharacterMode != "SelectedCharacter")
            return false;
        if (clock.UtcNow >= nextFishingRosterReadUtc)
        {
            nextFishingRosterReadUtc = clock.UtcNow.AddSeconds(30);
            fishingRosterAvailable = autoRetainer.TryGetRegisteredCharacters(out fishingRoster);
        }
        if (!fishingRosterAvailable)
            return false;
        var match = fishingRoster.FirstOrDefault(character => character.ContentId == session.OceanFishingCharacterContentId);
        if (match is null || match.Name != session.OceanFishingCharacterName || match.World != session.OceanFishingCharacterWorld)
            return false;
        target = match;
        return true;
    }

    private bool TryCurrentCharacter(out ulong contentId, out string name, out string world)
    {
        contentId = gameState.ContentId;
        name = gameState.CharacterName;
        world = gameState.HomeWorld;
        return gameState.IsLoggedIn && gameState.HasLocalPlayer && contentId != 0 &&
               !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(world);
    }

    private bool MatchesFishingCharacter(OceanFishingOperationRecord operation) => TryCurrentCharacter(out var cid, out var name, out var world) &&
        cid == operation.FishingCharacterContentId && name == operation.FishingCharacterName && world == operation.FishingCharacterWorld;

    private bool MatchesFarmingCharacter(OceanFishingOperationRecord operation) => TryCurrentCharacter(out var cid, out var name, out var world) &&
        cid == operation.FarmingCharacterContentId && name == operation.FarmingCharacterName && world == operation.FarmingCharacterWorld;

    private void OnDutyStarted()
    {
        if (Current is { State: "AwaitingBoarding" or "VoyageRunning" } operation && IsOceanTerritory(gameState.TerritoryType))
        {
            operation.DutyStarted = true;
            operation.OceanTerritoryEntered = true;
            sawOceanTerritory = true;
        }
    }

    private void OnDutyCompleted()
    {
        if (Current is not { } operation || !OwnsActivity ||
            !(IsOceanTerritory(gameState.TerritoryType) || sawOceanTerritory || operation.OceanTerritoryEntered))
            return;
        operation.DutyCompleted = true;
        SaveOrRecover("Ocean Fishing duty completion event");
    }

    private void MarkWindowHandled(DateTimeOffset windowStart)
    {
        configuration.HandledOceanFishingWindowsUtc ??= [];
        if (!configuration.HandledOceanFishingWindowsUtc.Contains(windowStart))
            configuration.HandledOceanFishingWindowsUtc.Add(windowStart);
        SaveOrRecover("record handled Ocean Fishing window");
    }

    private void SetState(OceanFishingOperationRecord operation, string state, string detail)
    {
        if (operation.State == state)
            return;
        var previous = operation.State;
        operation.State = state;
        operation.Detail = detail;
        operation.PhaseChangedUtc = clock.UtcNow;
        cleanupStableSinceUtc = null;
        returnStableSinceUtc = null;
        if (SaveOrRecover("Ocean Fishing phase transition"))
            log.Information("Ocean Fishing operation {OperationId} state {Previous}->{Current}: {Detail}",
                operation.OperationId, previous, state, detail);
    }

    private void RequireRecovery(string detail, Exception? error = null)
    {
        if (Current is not { } operation || operation.State == "RecoveryRequired")
            return;
        operation.State = "RecoveryRequired";
        operation.RecoveryKind ??= "AmbiguousProviderOutcome";
        operation.Detail = detail;
        operation.PhaseChangedUtc = clock.UtcNow;
        SetSessionRecovery(operation, detail);
        SaveOrRecover("Ocean Fishing recovery state");
        if (error is null)
            log.Error("Ocean Fishing operation {OperationId} requires recovery: {Detail}", operation.OperationId, detail);
        else
            log.Error(error, "Ocean Fishing operation {OperationId} requires recovery: {Detail}", operation.OperationId, detail);
    }

    private void SetSessionRecovery(OceanFishingOperationRecord operation, string detail)
    {
        if (configuration.AutomationSession is { } session && session.SessionId == operation.SessionId)
        {
            session.State = "RecoveryRequired";
            session.RecoveryKind ??= "OceanFishing";
            session.Detail = detail;
        }
    }

    private bool SaveOrRecover(string purpose)
    {
        try { save(); return true; }
        catch (Exception error)
        {
            if (Current is { } operation)
            {
                operation.State = "RecoveryRequired";
                operation.RecoveryKind = "PersistenceFailure";
                operation.Detail = $"Could not persist {purpose}; provider actions are blocked.";
                SetSessionRecovery(operation, operation.Detail);
            }
            log.Error(error, "Could not persist {Purpose} for Ocean Fishing.", purpose);
            return false;
        }
    }

    private static bool IsOceanTerritory(uint territory) => territory is OceanTerritoryOne or OceanTerritoryTwo;
}
