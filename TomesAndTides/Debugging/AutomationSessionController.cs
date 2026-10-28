namespace TomesAndTides.Debugging;

/// <summary>Persists the manual parent session independently from any weekly dungeon plan.</summary>
public sealed class AutomationSessionController(Configuration configuration, Action save, IWorkflowLog log, IUtcClock? clock = null)
{
    private IUtcClock Clock { get; } = clock ?? SystemUtcClock.Instance;
    public AutomationSessionRecord? Current => configuration.AutomationSession;
    public bool IsActive => Current is { State: "Active" or "StopRequested" };
    public bool NeedsRecovery => Current is { State: "RecoveryRequired" };

    /// <summary>Prepares the persisted session for coordinated recovery; caller must persist or invoke rollback.</summary>
    public Action? PrepareReloadResume(string farmingPlanId, ulong characterContentId, string characterName, string characterWorld)
    {
        if (Current is not { State: "RecoveryRequired", RecoveryKind: "Reload" } session ||
            session.FarmingPlanId != farmingPlanId || session.FarmingCharacterContentId != characterContentId ||
            !string.Equals(session.FarmingCharacterName, characterName, StringComparison.Ordinal) ||
            !string.Equals(session.FarmingCharacterWorld, characterWorld, StringComparison.Ordinal) ||
            configuration.RetainerSession is not { } retainer || retainer.SessionId != session.SessionId)
            return null;

        var oldSessionState = session.State;
        var oldSessionDetail = session.Detail;
        var oldSessionStop = session.StopRequested;
        var oldSessionRecovery = session.RecoveryKind;
        var oldRetainerState = retainer.State;
        var oldRetainerDetail = retainer.Detail;
        var oldRetainerStop = retainer.StopRequested;
        var oldRetainerRecovery = retainer.RecoveryKind;

        session.State = "Active";
        session.Detail = "Automation session explicitly resumed after safe reload recovery; captured settings and progress preserved.";
        session.StopRequested = false;
        session.RecoveryKind = null;
        retainer.State = "Active";
        retainer.Detail = "Retainer session explicitly resumed with its parent automation session; prior progress preserved.";
        retainer.StopRequested = false;
        retainer.RecoveryKind = null;

        return () =>
        {
            session.State = oldSessionState;
            session.Detail = oldSessionDetail;
            session.StopRequested = oldSessionStop;
            session.RecoveryKind = oldSessionRecovery;
            retainer.State = oldRetainerState;
            retainer.Detail = oldRetainerDetail;
            retainer.StopRequested = oldRetainerStop;
            retainer.RecoveryKind = oldRetainerRecovery;
        };
    }

    public AutomationSessionRecord StartOrResume(ulong farmingCharacterContentId, string farmingCharacterName,
        string farmingCharacterWorld, bool serviceRetainers, int retainerWaitMinutes, bool oceanFishingEnabled,
        int oceanFishingPreparationLeadMinutes, string fishingCharacterMode, ulong fishingCharacterContentId,
        string fishingCharacterName, string fishingCharacterWorld, string? farmingPlanId)
    {
        if (farmingCharacterContentId == 0 || string.IsNullOrWhiteSpace(farmingCharacterName) ||
            string.IsNullOrWhiteSpace(farmingCharacterWorld))
            throw new InvalidOperationException("Farming character identity is unavailable.");
        if (retainerWaitMinutes is < 0 or > 30 || oceanFishingPreparationLeadMinutes is < 0 or > 45)
            throw new InvalidOperationException("Captured automation timing is outside supported bounds.");
        if (oceanFishingEnabled && fishingCharacterMode == "SelectedCharacter" &&
            (fishingCharacterContentId == 0 || string.IsNullOrWhiteSpace(fishingCharacterName) ||
             string.IsNullOrWhiteSpace(fishingCharacterWorld)))
            throw new InvalidOperationException("Select a valid registered fishing character.");
        if (Current is { State: "RecoveryRequired" or "StopRequested" })
            throw new InvalidOperationException("Resolve or stop the existing automation session before starting another.");

        var prior = Current;
        var priorRetainerSession = configuration.RetainerSession;
        var session = prior is { State: "Active" } && prior.FarmingCharacterContentId == farmingCharacterContentId
            ? prior
            : new AutomationSessionRecord { SessionId = Guid.NewGuid().ToString("N"), CreatedUtc = Clock.UtcNow };
        session.State = "Active";
        session.Detail = "Explicitly confirmed parent automation session is active.";
        session.FarmingCharacterContentId = farmingCharacterContentId;
        session.FarmingCharacterName = farmingCharacterName;
        session.FarmingCharacterWorld = farmingCharacterWorld;
        session.FarmingPlanId = farmingPlanId;
        session.ServiceRetainers = serviceRetainers;
        session.RetainerWaitBeforeDungeonMinutes = retainerWaitMinutes;
        session.OceanFishingEnabled = oceanFishingEnabled;
        session.OceanFishingPreparationLeadMinutes = oceanFishingPreparationLeadMinutes;
        session.OceanFishingCharacterMode = fishingCharacterMode;
        session.OceanFishingCharacterContentId = fishingCharacterContentId;
        session.OceanFishingCharacterName = fishingCharacterName;
        session.OceanFishingCharacterWorld = fishingCharacterWorld;
        session.StopRequested = false;
        session.RecoveryKind = null;
        configuration.AutomationSession = session;
        var retainerSession = priorRetainerSession is { } oldSession && oldSession.SessionId == session.SessionId
            ? oldSession
            : new RetainerAutomationSessionRecord { SessionId = session.SessionId, CreatedUtc = Clock.UtcNow };
        retainerSession.State = "Active";
        retainerSession.Detail = "Retainer servicing is authorized by the active parent automation session.";
        retainerSession.FarmingCharacterContentId = farmingCharacterContentId;
        retainerSession.FarmingCharacterName = farmingCharacterName;
        retainerSession.FarmingCharacterWorld = farmingCharacterWorld;
        retainerSession.FarmingPlanId = farmingPlanId ?? session.SessionId;
        retainerSession.ServiceRetainers = serviceRetainers;
        retainerSession.ExpiresUtc = DateTimeOffset.MaxValue;
        retainerSession.StopRequested = false;
        configuration.RetainerSession = retainerSession;
        try { save(); }
        catch
        {
            configuration.AutomationSession = prior;
            configuration.RetainerSession = priorRetainerSession;
            throw;
        }
        log.Information("Automation session {SessionId} confirmed; fishing={Fishing}, retainers={Retainers}, dungeonPlan={PlanId}.",
            session.SessionId, session.OceanFishingEnabled, session.ServiceRetainers, session.FarmingPlanId ?? "none");
        return session;
    }

    public void RequestStop(string detail = "Manual Stop requested; no new activity will be admitted.")
    {
        if (Current is not { State: "Active" } session)
            return;
        session.State = "StopRequested";
        session.StopRequested = true;
        session.Detail = detail;
        if (configuration.RetainerSession is { } retainerSession && retainerSession.SessionId == session.SessionId)
        {
            retainerSession.StopRequested = true;
            retainerSession.State = "StopRequested";
            retainerSession.Detail = detail;
        }
        SaveOrBlock("automation Stop request");
    }

    public void CompleteStopIfSettled(bool anyOwnedActivity)
    {
        if (anyOwnedActivity || Current is not { State: "StopRequested" } session)
            return;
        session.State = "Stopped";
        session.Detail = "Stop completed after all owned activity settled.";
        if (configuration.RetainerSession is { } retainerSession && retainerSession.SessionId == session.SessionId)
        {
            retainerSession.State = "Stopped";
            retainerSession.StopRequested = true;
            retainerSession.Detail = session.Detail;
        }
        SaveOrBlock("automation Stop completion");
    }

    public void CompleteManualStop(string detail)
    {
        if (Current is not { } session)
            return;
        session.State = "Stopped";
        session.StopRequested = true;
        session.RecoveryKind = null;
        session.Detail = detail;
        if (configuration.RetainerSession is { } retainer && retainer.SessionId == session.SessionId)
        {
            retainer.State = "Stopped";
            retainer.StopRequested = true;
            retainer.RecoveryKind = null;
            retainer.Detail = detail;
        }
    }

    public void RecoverAfterReload()
    {
        if (Current is { State: "StopRequested", RecoveryKind: "ManualStop" } stopped)
        {
            stopped.State = "Stopped";
            stopped.StopRequested = true;
            stopped.RecoveryKind = null;
            stopped.Detail = "Manual Stop was persisted before reload. Provider cancellation outcome is unknown; verify providers before starting again.";
            SaveOrBlock("recover persisted manual Stop");
            return;
        }
        if (Current is not { State: "Active" or "StopRequested" } session)
            return;
        session.State = "RecoveryRequired";
        session.RecoveryKind = "Reload";
        session.Detail = "Plugin reloaded during the automation session. Inspect owned work; provider actions will not be replayed until explicit Resume.";
        if (configuration.RetainerSession is { } retainerSession && retainerSession.SessionId == session.SessionId)
        {
            retainerSession.State = "RecoveryRequired";
            retainerSession.Detail = session.Detail;
        }
        SaveOrBlock("automation reload recovery");
    }

    private void SaveOrBlock(string purpose)
    {
        try { save(); }
        catch (Exception error)
        {
            if (Current is { } session)
            {
                session.State = "RecoveryRequired";
                session.RecoveryKind = "PersistenceFailure";
                session.Detail = $"Could not persist {purpose}; new work is blocked.";
            }
            log.Error(error, "Automation session {SessionId} failed to persist {Purpose}.", Current?.SessionId ?? "unknown", purpose);
        }
    }
}
