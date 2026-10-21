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
    public bool CanResumeAutomationSession { get; private set; }

    public OceanFishingHandoffController(Configuration configuration, Action save, IWorkflowGameState gameState,
        IRetainerProvider autoRetainer, ILifestreamProvider lifestream, IWahToolsFeeshClient feesh,
        Func<bool> dungeonOperationOwned, Func<bool?> readAutoDutyStopped, IWorkflowLog log, IUtcClock? clock = null)
    {
        this.configuration = configuration;
        this.save = save;
        this.gameState = gameState;
        this.autoRetainer = autoRetainer;
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
    public string Status => Current?.Detail ?? "Monitoring Ocean Fishing windows";

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
            var session = configuration.AutomationSession;
            if (!allowNewWindow || session is not { State: "Active", StopRequested: false, OceanFishingEnabled: true })
                return;
            var now = clock.UtcNow;
            var window = OceanFishingSchedulePolicy.GetWindow(now, session.OceanFishingPreparationLeadMinutes);
            if (!OceanFishingSchedulePolicy.IsPreparationEligible(now, window) ||
                OceanFishingSchedulePolicy.IsWindowHandled(configuration.HandledOceanFishingWindowsUtc, window.StartUtc))
                return;

            if (now >= window.LaunchCutoffUtc)
            {
                MarkWindowHandled(window.StartUtc);
                return;
            }
            if (dungeonOperationOwned())
                return;
            if (!feesh.IsAvailable || !gameState.IsLoggedIn || !gameState.HasLocalPlayer ||
                !gameState.IsSafePlayerState || readAutoDutyStopped() != true ||
                !autoRetainer.TryGetProviderState(out var retainerBusy, out var retainerActive) ||
                retainerBusy || retainerActive || !lifestream.TryGetBusy(out var lifestreamBusy) || lifestreamBusy)
                return;
            if (!TrySelectFishingCharacter(session, out var target))
                return;
            var status = feesh.ReadStatus();
            if (!status.IsClean || now - status.ObservedUtc > StatusFreshness)
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
                PhaseChangedUtc = now,
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
            RequireRecovery($"Unexpected Ocean Fishing coordinator error: {error.GetBaseException().Message}", error);
        }
    }

    public void Dispose()
    {
        disposed = true;
        gameState.DutyStarted -= OnDutyStarted;
        gameState.DutyCompleted -= OnDutyCompleted;
    }

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
        if (!MatchesFishingCharacter(operation) || !gameState.IsSafePlayerState || readAutoDutyStopped() != true ||
            !autoRetainer.TryGetProviderState(out var busy, out var active) || busy || active ||
            !lifestream.TryGetBusy(out var lifestreamBusy) || lifestreamBusy)
            return;
        var status = feesh.ReadStatus();
        if (!status.IsClean || clock.UtcNow - status.ObservedUtc > StatusFreshness)
            return;

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
        if (session.OceanFishingCharacterMode != "SelectedCharacter" ||
            !autoRetainer.TryGetRegisteredCharacters(out var roster))
            return false;
        var match = roster.FirstOrDefault(character => character.ContentId == session.OceanFishingCharacterContentId);
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
