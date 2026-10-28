using System.Text.Json;
using TomesAndTides;
using TomesAndTides.Debugging;
using TomesAndTides.Integrations;

var tests = new (string Name, Action Run)[]
{
    ("dungeon launch and verified reward create linked retainer checkpoint", TestDungeonLifecycleCreatesCheckpoint),
    ("verified dungeon checkpoint gates next run through retainer return", TestVerifiedCheckpointAndContinuation),
    ("unknown venture readiness blocks checkpoint and dungeon continuation", TestUnknownReadinessBlocks),
    ("persisted completed handoff reconciles checkpoint after reload without replay", TestReloadReconcilesCompletedHandoff),
    ("ambiguous retainer launch reload requires recovery without replay", TestReloadDoesNotReplayLaunchIntent),
    ("checkpoint persistence failure prevents dungeon continuation", TestCheckpointPersistenceFailure),
    ("synchronous AutoRetainer completion event is retained", TestSynchronousCompletion),
    ("AutoDuty mode is prepared and one loop requested despite prior settings", TestAutoDutyPreparedForSingleRun),
    ("failed AutoDuty mode write waits and retries without failed runs", TestAutoDutyModeWriteRetries),
    ("unknown or busy provider waits before dispatch then resumes", TestPrelaunchProviderWaitResumes),
    ("Stop during near-complete wait prevents new provider work", TestStopDuringVentureWait),
    ("unsafe farming-character return verification times out without retry", TestReturnVerificationTimeout),
    ("near-complete wait services venture when it becomes ready", TestVentureBecomesReady),
    ("venture beyond wait window permits continuation without an AR pass", TestVentureOutsideWait),
    ("disabled service clears checkpoint without requesting AutoRetainer", TestServiceDisabled),
    ("reload resume restores parent session and captured fishing settings", TestReloadResumesParentSession),
    ("failed coordinated resume restores recovery state", TestReloadResumePersistenceFailure),
    ("ambiguous fishing recovery blocks farming resume", TestAmbiguousFishingBlocksResume),
    ("safe fishing recovery acknowledgment persists and never relaunches", TestFishingRecoveryAcknowledgedSafely),
    ("fishing recovery acknowledgment revalidates safety on framework tick", TestFishingRecoveryAcknowledgementRevalidates),
    ("failed fishing recovery acknowledgment save preserves recovery", TestFishingRecoveryAcknowledgementSaveFailure),
    ("manual Stop cancels owned retainer work without character return", TestManualStopCancelsRetainerImmediately),
    ("manual Stop cancels TT-owned WahTools without voyage replay", TestManualStopCancelsOwnedWahTools),
    ("manual Stop clears fishing recovery without requiring safe return", TestManualStopClearsFishingRecovery),
    ("manual Stop records uncertain provider cancellation and preserves credited runs", TestManualStopRecordsCancellationWarnings),
    ("manual Stop cancels queued dungeon before AutoDuty dispatch", TestManualStopCancelsPendingDungeon),
    ("manual Stop keeps launched dungeon observer attached", TestManualStopObservesLaunchedDungeon),
    ("same-period checklist target survives refresh", TestSamePeriodChecklistPreserved),
    ("new period creates one fresh checklist from live tome count", TestPeriodRolloverCreatesChecklist),
    ("active run repairs stale checklist pointer without losing progress", TestActiveRunRepairsChecklistPointer),
    ("untouched legacy future allocation is recalculated", TestLegacyFutureTargetRecalculated),
    ("new session waits for active AutoRetainer after immediate Stop", TestManualStopStillGatesRestartOnProviderState),
    ("persisted manual Stop reload finalizes without replaying provider actions", TestManualStopReloadDoesNotReplay),
    ("uninitialized WahTools trip is treated as idle", TestUninitializedTripIsIdle),
    ("Ocean Fishing launches once during an active window with completed dungeon allocation", TestOceanFishingLaunchDuringWindow),
    ("early Ocean Fishing launch remains pending through delayed actual boarding", TestEarlyOceanFishingLaunchBoardsAfterOldTimeout),
    ("Ocean Fishing matching boarding at deadline wins over timeout", TestOceanFishingBoardingWinsAtDeadline),
    ("Ocean Fishing timeout deadline enters recovery without relaunch", TestOceanFishingBoardingDeadlineRecovery),
    ("unrelated duty does not count as Ocean Fishing boarding", TestUnrelatedDutyDoesNotCountAsFishingBoarding),
    ("Ocean Fishing territory without duty entry still times out", TestOceanTerritoryWithoutDutyStillTimesOut),
    ("Ocean Fishing cutoff miss is recorded before schedule advances", TestOceanFishingMissedCutoff),
    ("reload recovery can be stopped independently of Resume eligibility", TestReloadRecoveryCanStop),
    ("recovery stop preserves unresolved fishing ownership", TestRecoveryStopPreservesFishingOwnership),
    ("acknowledged recovery allows new same-week plan", TestAcknowledgedRecoveryAllowsNewPlan),
    ("fishing priority defers a new retainer checkpoint pass", TestFishingPriorityDefersRetainerCheckpoint),
    ("unobserved past fishing window is not reported as missed", TestUnobservedFishingWindowIsNotMissed),
    ("observed fishing window cutoff is reported once", TestObservedFishingWindowMissedOnce),
    ("normal inactive and ownership waits are informational", TestFishingWaitDiagnosticsAreInformational),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {error}");
    }
}
return failed == 0 ? 0 : 1;

static void TestVerifiedCheckpointAndContinuation()
{
    var h = new Harness(known: true, ready: true);
    h.TickAll();
    Equal(1, h.Configuration.FarmingPlan!.Periods.Single().CompletedRuns, "verified dungeon credited once");
    Equal(1, h.Configuration.FarmingPlan.CreditedOperationIds.Count(id => id == "run-1"), "operation ledger idempotent");
    Equal(1, h.Retainers.PassRequests, "one AR request at checkpoint");
    Equal("AwaitingStart", h.Configuration.RetainerHandoff!.State, "pass awaiting start");
    Equal(0, h.AutoDuty.RunRequests, "checkpoint blocks dungeon");

    h.Retainers.Busy = true;
    h.Retainers.Active = true;
    h.TickAll();
    Equal("RunningPass", h.Configuration.RetainerHandoff!.State, "owned pass observed");

    h.Game.Name = "Venture Alt";
    h.Retainers.Busy = false;
    h.Retainers.Active = false;
    h.Retainers.FinishPass();
    h.TickAll();
    Equal("WaitingForProviderIdle", h.Configuration.RetainerHandoff!.State, "completion observed");
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
    Equal(1, h.Lifestream.ReturnRequests, "one farming-character return");
    Equal("AwaitingFarmingCharacter", h.Configuration.RetainerHandoff!.State, "return accepted");
    h.TickAll();
    Equal("VerifyingReturn", h.Configuration.RetainerHandoff!.State, "farming character observed");
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
    Equal("Completed", h.Configuration.RetainerHandoff!.State, "return settled");
    Equal(null, h.Configuration.FarmingPlan!.RetainerCheckpointOperationId, "checkpoint cleared");
    True(h.Configuration.FarmingPlan.CompletedRetainerCheckpointOperationIds.Contains("run-1"), "checkpoint ledger credited");
    Equal(0, h.AutoDuty.RunRequests, "continuation waits for next coordinator tick");

    h.EligibilityStatus = "Unknown";
    h.TickAll();
    True(h.Dungeon.Current is null, "unknown fresh eligibility prevents next run");
    h.EligibilityStatus = "Eligible";
    h.Clock.Advance(TimeSpan.FromSeconds(31));
    h.TickAll();
    True(h.Dungeon.Current is { State: "PendingValidation" }, "next run queued after checkpoint");
    h.TickAll();
    h.TickAll();
    Equal(1, h.AutoDuty.RunRequests, "exactly one next dungeon request");
    True(h.EligibilityChecks >= 2, "fresh eligibility checked by coordinator and run dispatch");
    Equal(1, h.Retainers.PassRequests, "no duplicate AR pass");
}

static void TestDungeonLifecycleCreatesCheckpoint()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    h.TickAll();
    True(h.Dungeon.Current is { State: "PendingValidation" }, "plan queues one run");
    var operationId = h.Dungeon.Current!.OperationId;
    Equal(0, h.AutoDuty.RunRequests, "dispatch waits one coordinator update");
    h.TickAll();
    Equal(1, h.AutoDuty.RunRequests, "AutoDuty called once");
    True(h.Dungeon.Current is { State: "AwaitingDuty" }, "run awaits matching duty entry");

    h.Game.ContentFinderConditionId = 456;
    h.Game.TerritoryType = 321;
    h.Game.RaiseDutyStarted();
    h.Dungeon.Tick();
    Equal("RunningDuty", h.Dungeon.Current!.State, "matching duty observed");
    h.Game.RaiseDutyCompleted();
    h.Game.IsDutyStarted = false;
    h.Game.ContentFinderConditionId = 0;
    h.Game.TerritoryType = 100;
    h.AutoDuty.Stopped = true;
    h.Dungeon.Tick();
    Equal("WaitingForReward", h.Dungeon.Current!.State, "duty exit observed");

    h.WeeklyAcquired = 150;
    h.Dungeon.Tick();
    Equal("Completed", h.Dungeon.Current!.State, "reward reconciled");
    Equal("Verified", h.Dungeon.Current!.RewardOutcome, "exact reward verified");
    h.TickAll();
    Equal(1, h.Configuration.FarmingPlan!.Periods.Single().CompletedRuns, "run credited exactly once");
    Equal(operationId, h.Configuration.FarmingPlan.RetainerCheckpointOperationId, "verified run creates linked checkpoint");
    Equal(1, h.Retainers.PassRequests, "checkpoint starts one retainer pass");
    CompleteHandoff(h);
    Equal(null, h.Configuration.FarmingPlan.RetainerCheckpointOperationId, "return clears matching checkpoint");
    h.TickAll();
    True(h.Dungeon.Current is { State: "PendingValidation" }, "freshly eligible next dungeon queued");
    h.TickAll();
    Equal(2, h.AutoDuty.RunRequests, "second dungeon requested exactly once after handoff");
}

static void TestUnknownReadinessBlocks()
{
    var h = new Harness(known: false, ready: false);
    h.TickAll();
    h.TickAll();
    Equal(0, h.Retainers.PassRequests, "unknown readiness never starts AR");
    Equal("run-1", h.Configuration.FarmingPlan!.RetainerCheckpointOperationId, "unknown readiness preserves checkpoint");
    Equal(0, h.AutoDuty.RunRequests, "unknown readiness blocks dungeon");
}

static void TestReloadReconcilesCompletedHandoff()
{
    var h = new Harness(known: true, ready: false);
    h.Retainers.ClosestEligibleSeconds = 300;
    h.TickAll();
    Equal("run-1", h.Configuration.FarmingPlan!.RetainerCheckpointOperationId, "verified run creates checkpoint");
    h.Configuration.RetainerHandoff = new RetainerHandoffRecord
    {
        HandoffId = "completed-handoff", SessionId = "session", FarmingOperationId = "run-1",
        State = "Completed", CompletionEventObserved = true, PhaseChangedUtc = h.Clock.UtcNow,
    };
    h.PersistSnapshot();
    var saved = h.Snapshot();
    var reloaded = new Harness(known: true, ready: false, JsonSerializer.Deserialize<Configuration>(saved));
    reloaded.Farming.RecoverAfterReload();
    reloaded.Handoff.RecoverAfterReload();
    Equal(null, reloaded.Configuration.FarmingPlan!.RetainerCheckpointOperationId, "completed checkpoint reconciled");
    Equal(0, reloaded.Retainers.PassRequests, "reload does not replay AR");
    Equal(0, reloaded.Lifestream.ReturnRequests, "reload does not replay return");
    Equal("RecoveryRequired", reloaded.Configuration.FarmingPlan.State, "plan retains explicit reload recovery");
}

static void TestReloadDoesNotReplayLaunchIntent()
{
    var h = new Harness(known: true, ready: false);
    h.Retainers.ClosestEligibleSeconds = 300;
    h.TickAll();
    h.Configuration.RetainerHandoff = new RetainerHandoffRecord
    {
        HandoffId = "ambiguous", SessionId = "session", FarmingOperationId = "run-1",
        State = "LaunchIntentRecorded", LaunchIntentUtc = h.Clock.UtcNow,
        PhaseChangedUtc = h.Clock.UtcNow,
    };
    h.PersistSnapshot();
    var reloaded = new Harness(true, true, JsonSerializer.Deserialize<Configuration>(h.Snapshot()));
    reloaded.Farming.RecoverAfterReload();
    reloaded.Handoff.RecoverAfterReload();
    Equal("RecoveryRequired", reloaded.Configuration.RetainerHandoff!.State, "ambiguous handoff is recovery-required");
    Equal(0, reloaded.Retainers.PassRequests, "no AutoRetainer request replay");
    Equal(0, reloaded.Lifestream.ReturnRequests, "no Lifestream request replay");
}

static void TestCheckpointPersistenceFailure()
{
    var h = new Harness(known: true, ready: true) { FailCheckpointClearSave = true };
    CompleteHandoff(h);
    Equal("RecoveryRequired", h.Configuration.FarmingPlan!.State, "failed persistence puts farming plan in recovery");
    Equal(0, h.AutoDuty.RunRequests, "failed checkpoint save blocks AutoDuty");
    Equal(1, h.Retainers.PassRequests, "no repeated AR request");
}

static void TestSynchronousCompletion()
{
    var h = new Harness(known: true, ready: true);
    h.Retainers.FinishSynchronously = true;
    h.TickAll();
    Equal(1, h.Retainers.PassRequests, "one pass request");
    h.TickAll();
    True(h.Configuration.RetainerHandoff!.CompletionEventObserved, "synchronous completion was observed");
    True(h.Configuration.RetainerHandoff.State != "RecoveryRequired", "synchronous completion is not treated as lost ownership");
}

static void TestAutoDutyPreparedForSingleRun()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    h.AutoDuty.ConfiguredLoopCount = 8;
    h.TickAll(); // queue run
    h.TickAll(); // prepare mode and dispatch

    Equal(1, h.AutoDuty.ModeWriteRequests, "selected Support mode is applied automatically");
    Equal("Support", h.AutoDuty.DutyMode, "configured AutoDuty duty mode matches TT selection");
    Equal(1, h.AutoDuty.RunRequests, "AutoDuty dispatched once");
    Equal(1, h.AutoDuty.RunLoopCount, "dispatch explicitly requests exactly one loop");
    Equal(1, h.AutoDuty.ConfiguredLoopCount, "AutoDuty Run applies single-loop count");
    True(h.Dungeon.Current is { State: "AwaitingDuty" }, "run starts without manual AutoDuty setup");
}

static void TestAutoDutyModeWriteRetries()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    h.AutoDuty.ModeWriteFailures = 1;
    h.TickAll(); // queue run
    h.TickAll(); // first mode write fails
    Equal("PendingValidation", h.Dungeon.Current!.State, "mode failure remains prelaunch pending");
    Equal(0, h.AutoDuty.RunRequests, "failed mode preparation never dispatches");
    Equal(0, h.Configuration.CancelledDungeonRuns.Count, "mode failure does not create failed/cancelled run history");

    h.TickAll();
    Equal(1, h.AutoDuty.ModeWriteRequests, "poll interval prevents repeated immediate writes");
    h.Clock.Advance(TimeSpan.FromSeconds(5));
    h.TickAll();
    Equal(2, h.AutoDuty.ModeWriteRequests, "mode preparation retries after wait");
    Equal(1, h.AutoDuty.RunRequests, "dispatch resumes after mode write succeeds");
    Equal("AwaitingDuty", h.Dungeon.Current!.State, "operation transitions to launched state");
}

static void TestPrelaunchProviderWaitResumes()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    h.TickAll(); // queue run
    h.Retainers.Known = false;
    h.TickAll();
    Equal("PendingValidation", h.Dungeon.Current!.State, "unknown provider state waits without failure");
    Equal(0, h.AutoDuty.RunRequests, "unknown provider state blocks dispatch");
    var firstReason = h.Dungeon.Current.Detail;
    h.TickAll();
    Equal(firstReason, h.Dungeon.Current!.Detail, "unchanged wait reason remains stable");
    Equal(0, h.Configuration.CancelledDungeonRuns.Count, "provider wait creates no failed run history");

    h.Retainers.Known = true;
    h.Clock.Advance(TimeSpan.FromSeconds(5));
    h.TickAll();
    Equal(1, h.AutoDuty.RunRequests, "provider idle state automatically resumes dispatch");
    Equal("AwaitingDuty", h.Dungeon.Current!.State, "pending run transitions after provider clears");
}

static void TestStopDuringVentureWait()
{
    var h = new Harness(known: true, ready: false);
    h.Retainers.ClosestEligibleSeconds = 300;
    h.TickAll();
    Equal(0, h.Retainers.PassRequests, "near-complete venture waits without an early pass");
    h.ManualStop.RequestStop();
    h.TickAll();
    Equal("Stopped", h.Configuration.AutomationSession!.State, "manual Stop completes in one framework update");
    Equal(0, h.Retainers.PassRequests, "Stop prevents pass request");
    Equal(0, h.AutoDuty.RunRequests, "Stop prevents dungeon request");
}

static void TestManualStopCancelsRetainerImmediately()
{
    var h = new Harness(known: true, ready: true);
    h.TickAll();
    h.Retainers.Busy = true;
    h.Retainers.Active = true;
    h.TickAll();
    Equal("RunningPass", h.Configuration.RetainerHandoff!.State, "TT owns active retainer pass");
    h.Game.Name = "Retainer Alt";

    h.ManualStop.RequestStop();
    h.TickAll();

    Equal("Stopped", h.Configuration.AutomationSession!.State, "parent session stopped immediately");
    Equal("Stopped", h.Configuration.RetainerSession!.State, "retainer session stopped immediately");
    Equal(null, h.Configuration.RetainerHandoff, "active handoff no longer owns TT");
    Equal(1, h.Retainers.CancelRequests, "TT-owned AutoRetainer pass cancelled once");
    Equal(0, h.Lifestream.ReturnRequests, "manual Stop never requests farming-character return");
    True(h.Configuration.CancelledRetainerHandoffs.Single().State == "CancelledByUser", "cancelled handoff preserved as history");
}

static void TestManualStopClearsFishingRecovery()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    SeedFishingRecovery(h);
    h.Configuration.OceanFishingOperation!.FishingCharacterContentId = h.Game.ContentId;
    h.Configuration.OceanFishingOperation.FishingCharacterName = h.Game.Name;
    h.Configuration.OceanFishingOperation.FishingCharacterWorld = h.Game.World;
    h.Ocean.RecoverAfterReload();
    h.Game.Name = "Different Character";

    h.ManualStop.RequestStop();
    h.TickAll();

    Equal("Stopped", h.Configuration.AutomationSession!.State, "recovery parent stopped immediately");
    Equal(null, h.Configuration.OceanFishingOperation, "fishing ownership cleared without safe-return acknowledgment");
    True(h.Configuration.CancelledOceanFishingOperations.Single().State == "CancelledByUser", "fishing cancellation retained as history");
    True(h.Configuration.HandledOceanFishingWindowsUtc!.Contains(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero)),
        "ambiguous launched window remains handled");
    Equal(0, h.Feesh.LaunchCalls, "Stop never replays voyage");
}

static void TestManualStopCancelsOwnedWahTools()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    h.Configuration.AutomationSession!.OceanFishingEnabled = true;
    h.Configuration.AutomationSession.OceanFishingPreparationLeadMinutes = 15;
    h.Clock.Advance(TimeSpan.FromMinutes(45));
    h.Ocean.Tick();
    h.Ocean.Tick();
    Equal(1, h.Feesh.LaunchCalls, "one TT-owned WahTools launch issued");
    Equal("AwaitingBoarding", h.Configuration.OceanFishingOperation!.State, "operation owns pending boarding");

    h.ManualStop.RequestStop();
    h.TickAll();

    Equal(1, h.Feesh.LaunchCalls, "manual Stop never relaunches voyage");
    Equal(1, h.Feesh.StopCalls, "manual Stop requests WahTools cancellation once");
    Equal(null, h.Configuration.OceanFishingOperation, "fishing ownership cleared immediately");
    True(h.Configuration.CancelledOceanFishingOperations.Single().State == "CancelledByUser", "cancelled voyage remains in audit history");
    True(h.Configuration.HandledOceanFishingWindowsUtc.Contains(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero)),
        "launched fishing window is retained as handled");
}

static void TestManualStopRecordsCancellationWarnings()
{
    var h = new Harness(known: true, ready: true);
    h.TickAll();
    h.Retainers.Busy = true;
    h.Retainers.Active = true;
    h.TickAll();
    h.Retainers.CancelAccepted = false;
    var credited = h.Configuration.FarmingPlan!.CreditedOperationIds.ToArray();
    h.AutoDuty.Stopped = false;

    h.ManualStop.RequestStop();
    h.TickAll();

    Equal("Stopped", h.Configuration.AutomationSession!.State, "provider cancellation failure does not hold TT stopped state");
    True(h.Configuration.AutomationSession.Detail.Contains("AutoRetainer may still be running", StringComparison.Ordinal),
        "provider cancellation failure is visible");
    True(h.Configuration.AutomationSession.Detail.Contains("AutoDuty was not stopped", StringComparison.Ordinal),
        "AutoDuty non-cancellation is explicit");
    True(credited.SequenceEqual(h.Configuration.FarmingPlan!.CreditedOperationIds), "verified dungeon credits preserved");
}

static void TestManualStopCancelsPendingDungeon()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    h.TickAll();
    True(h.Dungeon.Current is { State: "PendingValidation" }, "plan has queued but not dispatched dungeon");

    h.ManualStop.RequestStop();
    h.TickAll();

    Equal(0, h.AutoDuty.RunRequests, "pending dungeon never reaches AutoDuty");
    Equal(null, h.Dungeon.Current, "pending run no longer blocks TT");
    Equal("CancelledByUser", h.Configuration.CancelledDungeonRuns.Single().State, "pending run cancellation retained");
    Equal("Stopped", h.Configuration.FarmingPlan!.State, "farming plan stopped immediately");
}

static void TestManualStopObservesLaunchedDungeon()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    h.TickAll(); // queue
    h.TickAll(); // dispatch
    Equal(1, h.AutoDuty.RunRequests, "dungeon dispatched before Stop");
    var operationId = h.Dungeon.Current!.OperationId;

    h.ManualStop.RequestStop();
    h.TickAll();

    Equal(operationId, h.Dungeon.Current!.OperationId, "launched dungeon remains tracked after Stop");
    Equal("AwaitingDuty", h.Dungeon.Current.State, "Stop does not overwrite observer state");
    Equal(operationId, h.Configuration.FarmingPlan!.CurrentOperationId, "checklist retains active operation link");
    Equal(1, h.AutoDuty.RunRequests, "Stop and later observations never dispatch another run");

    h.Game.ContentFinderConditionId = 456;
    h.Game.TerritoryType = 321;
    h.Game.RaiseDutyStarted();
    h.Dungeon.Tick();
    h.Game.RaiseDutyCompleted();
    h.Game.IsDutyStarted = false;
    h.Game.ContentFinderConditionId = 0;
    h.Game.TerritoryType = 100;
    h.AutoDuty.Stopped = true;
    h.Dungeon.Tick();
    h.WeeklyAcquired = 150;
    h.Dungeon.Tick(); // reward reconciliation
    h.Farming.Tick(); // stopped scheduler credits verified completion only

    Equal(1, h.Configuration.FarmingPlan.Periods.Single().CompletedRuns, "completed stopped dungeon credited once");
    Equal(null, h.Configuration.FarmingPlan.CurrentOperationId, "observer link released after credit");
    Equal(null, h.Configuration.FarmingPlan.RetainerCheckpointOperationId, "Stop prevents post-dungeon handoff");
    Equal(1, h.AutoDuty.RunRequests, "stopped scheduler does not launch follow-up dungeon");
}

static void TestSamePeriodChecklistPreserved()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    var checklist = h.Configuration.FarmingPlan!.Periods.Single();
    checklist.AllocatedRuns = 3;
    checklist.CompletedRuns = 2;
    h.WeeklyAcquired = 850; // actual weekly remainder would now imply only one run

    h.Farming.Tick(externalCharacterHandoffOwns: false, allowDispatch: false);

    Equal(3, h.Configuration.FarmingPlan.Periods.Single().AllocatedRuns, "same-period target is stable despite changed live tome count");
    Equal(2, h.Configuration.FarmingPlan.Periods.Single().CompletedRuns, "completed checklist count survives refresh");
}

static void TestPeriodRolloverCreatesChecklist()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    h.Clock.Advance(TimeSpan.FromDays(2));
    h.WeeklyAcquired = 100; // 16 runs remain; five daily periods remain at this point

    h.Farming.Tick(externalCharacterHandoffOwns: false, allowDispatch: false);

    var plan = h.Configuration.FarmingPlan!;
    var current = plan.Periods.Single();
    Equal(FarmingSchedulePolicy.GetPeriodStartUtc(h.Clock.UtcNow, plan.WeekStartUtc), current.StartUtc,
        "old checklist replaced by current UTC period");
    Equal(4, current.AllocatedRuns, "16 runs over four remaining daily periods gives target four");
    Equal(0, current.CompletedRuns, "new period checklist begins at zero");
}

static void TestActiveRunRepairsChecklistPointer()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    var plan = h.Configuration.FarmingPlan!;
    var today = FarmingSchedulePolicy.GetPeriodStartUtc(h.Clock.UtcNow, plan.WeekStartUtc);
    var yesterday = today.AddDays(-1);
    var old = new FarmingPeriodRecord { StartUtc = yesterday, AllocatedRuns = 3, CompletedRuns = 3 };
    var current = new FarmingPeriodRecord { StartUtc = today, AllocatedRuns = 3, CompletedRuns = 2,
        CreditedOperationIds = ["run-a", "run-b"] };
    plan.CurrentPeriodStartUtc = yesterday;
    plan.Periods = [old, current];
    plan.CurrentOperationId = "run-c";
    h.Configuration.DungeonRun = new DungeonRunRecord
    {
        OperationId = "run-c", FarmingPlanId = plan.PlanId, FarmingPeriodStartUtc = today,
        State = "RunningDuty", RewardOutcome = "Unknown", DungeonName = "Test Dungeon",
    };

    h.Farming.Tick(externalCharacterHandoffOwns: false, allowDispatch: false);

    Equal(today, plan.CurrentPeriodStartUtc!.Value, "active run period becomes authoritative pointer");
    Equal(1, plan.Periods.Count, "legacy period records collapse to one current checklist");
    Equal(2, h.Farming.CurrentChecklist!.CompletedRuns, "current progress survives pointer repair");
    Equal(3, h.Farming.CurrentChecklist.AllocatedRuns, "current target survives pointer repair");
}

static void TestLegacyFutureTargetRecalculated()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    var plan = h.Configuration.FarmingPlan!;
    var today = FarmingSchedulePolicy.GetPeriodStartUtc(h.Clock.UtcNow, plan.WeekStartUtc);
    plan.CurrentPeriodStartUtc = today.AddDays(-1);
    plan.Periods =
    [
        new FarmingPeriodRecord { StartUtc = today.AddDays(-1), AllocatedRuns = 3, CompletedRuns = 3 },
        new FarmingPeriodRecord { StartUtc = today, AllocatedRuns = 1 },
        new FarmingPeriodRecord { StartUtc = today.AddDays(1), AllocatedRuns = 2 },
    ];

    h.Farming.Tick(externalCharacterHandoffOwns: false, allowDispatch: false);

    var expected = FarmingSchedulePolicy.CurrentPeriodTarget(16, today, plan.FinishDeadlineUtc);
    Equal(today, plan.CurrentPeriodStartUtc!.Value, "current period replaces stale pointer");
    Equal(1, plan.Periods.Count, "future allocations are removed from checklist state");
    Equal(expected, plan.Periods.Single().AllocatedRuns, "unused legacy future allocation is recalculated from current weekly tomes");
}

static void TestManualStopStillGatesRestartOnProviderState()
{
    var h = new Harness(known: true, ready: true);
    h.TickAll();
    var issuedPasses = h.Retainers.PassRequests;
    h.ManualStop.RequestStop();
    h.TickAll();
    h.Configuration.FarmingPlan = null;
    h.Configuration.DungeonRun = null;
    h.Retainers.Busy = true;
    h.Retainers.Active = true;
    h.Automation.StartOrResume(123, "Farm Main", "Test World", true, 5,
        false, 15, "CurrentCharacter", 0, string.Empty, string.Empty, null);

    h.Handoff.Tick();

    Equal(issuedPasses, h.Retainers.PassRequests, "new session does not dispatch while AutoRetainer remains active");
    Equal("Active", h.Configuration.AutomationSession!.State, "temporary busy provider does not force recovery");
    True(h.Configuration.AutomationSession.Detail.Contains("Waiting before provider work", StringComparison.Ordinal),
        "UI-visible session detail explains the wait");
    Equal(issuedPasses, h.Retainers.PassRequests, "active provider receives no duplicate request");
    h.Retainers.Busy = false;
    h.Retainers.Active = false;
    h.Clock.Advance(TimeSpan.FromSeconds(5));
    h.Handoff.Tick();
    Equal(issuedPasses + 1, h.Retainers.PassRequests, "provider work continues automatically when idle");
    True(!h.Configuration.AutomationSession.Detail.Contains("Waiting before provider work", StringComparison.Ordinal),
        "UI-visible wait clears after providers become idle");
}

static void TestManualStopReloadDoesNotReplay()
{
    var original = new Harness(known: true, ready: false, seedCompletedRun: false);
    original.Configuration.RetainerHandoff = new RetainerHandoffRecord
    {
        HandoffId = "stop-retainer", SessionId = "session", State = "ManualStopPending",
        RecoveryKind = "ManualStop", LaunchIntentUtc = original.Clock.UtcNow,
    };
    original.Configuration.OceanFishingOperation = new OceanFishingOperationRecord
    {
        OperationId = "stop-fishing", State = "ManualStopPending", RecoveryKind = "ManualStop",
        LaunchIntentUtc = original.Clock.UtcNow, WindowStartUtc = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero),
    };
    original.Configuration.DungeonRun = new DungeonRunRecord
    {
        OperationId = "stop-dungeon", State = "ManualStopPending", FarmingPlanId = "plan",
    };
    original.Configuration.AutomationSession!.State = "StopRequested";
    original.Configuration.AutomationSession.StopRequested = true;
    original.Configuration.AutomationSession.RecoveryKind = "ManualStop";
    original.Configuration.RetainerSession!.State = "StopRequested";
    original.Configuration.RetainerSession.StopRequested = true;
    original.Configuration.RetainerSession.RecoveryKind = "ManualStop";
    original.Configuration.FarmingPlan!.RecoveryKind = "ManualStop";
    original.PersistSnapshot();

    var restored = JsonSerializer.Deserialize<Configuration>(original.Snapshot())!;
    var reloaded = new Harness(known: true, ready: false, restored, seedCompletedRun: false);
    reloaded.Automation.RecoverAfterReload();
    reloaded.Farming.RecoverAfterReload();
    reloaded.Handoff.RecoverAfterReload();
    reloaded.Ocean.RecoverAfterReload();

    Equal("Stopped", restored.AutomationSession!.State, "persisted parent Stop finalizes after reload");
    Equal("Stopped", restored.RetainerSession!.State, "persisted retainer Stop finalizes after reload");
    Equal("Stopped", restored.FarmingPlan!.State, "persisted plan Stop finalizes after reload");
    Equal(null, restored.RetainerHandoff, "retainer ownership cleared after reload");
    Equal(null, restored.OceanFishingOperation, "fishing ownership cleared after reload");
    Equal(null, restored.DungeonRun, "dungeon ownership cleared after reload");
    Equal(0, reloaded.Retainers.CancelRequests, "reload recovery never replays cancellation IPC");
    Equal(0, reloaded.Feesh.StopCalls, "reload recovery never replays WahTools Stop");
    Equal(0, reloaded.Lifestream.AbortRequests, "reload recovery never replays Lifestream Abort");
}

static void TestReturnVerificationTimeout()
{
    var h = new Harness(known: true, ready: true);
    h.TickAll();
    h.Retainers.Busy = true;
    h.Retainers.Active = true;
    h.TickAll();
    h.Game.Name = "Venture Alt";
    h.Retainers.Busy = false;
    h.Retainers.Active = false;
    h.Retainers.FinishPass();
    h.TickAll();
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
    Equal(1, h.Lifestream.ReturnRequests, "one return was issued");
    h.Game.InCombat = true;
    h.TickAll();
    Equal("VerifyingReturn", h.Configuration.RetainerHandoff!.State, "return enters verification");
    h.Clock.Advance(TimeSpan.FromMinutes(10));
    h.TickAll();
    Equal("RecoveryRequired", h.Configuration.RetainerHandoff!.State, "verification timeout requires recovery");
    Equal(1, h.Lifestream.ReturnRequests, "timeout does not repeat return request");
    Equal(0, h.AutoDuty.RunRequests, "unsafe return blocks dungeon");
}

static void TestVentureBecomesReady()
{
    var h = new Harness(known: true, ready: false);
    h.Retainers.ClosestEligibleSeconds = 300;
    h.TickAll();
    Equal(0, h.Retainers.PassRequests, "near-complete venture waits");
    Equal(0, h.AutoDuty.RunRequests, "near-complete wait blocks dungeon");
    h.Retainers.Ready = true;
    h.Retainers.ClosestEligibleSeconds = 0;
    h.TickAll();
    Equal(1, h.Retainers.PassRequests, "newly ready venture starts one pass");
    Equal("run-1", h.Configuration.FarmingPlan!.RetainerCheckpointOperationId, "checkpoint remains until pass return");
}

static void TestServiceDisabled()
{
    var h = new Harness(known: true, ready: true);
    h.Configuration.AutomationSession!.ServiceRetainers = false;
    h.TickAll();
    Equal(0, h.Retainers.PassRequests, "disabled setting prevents AutoRetainer request");
    Equal(null, h.Configuration.FarmingPlan!.RetainerCheckpointOperationId, "disabled service clears checkpoint");
    Equal(0, h.AutoDuty.RunRequests, "dungeon is queued only on subsequent tick");
}

static void TestReloadResumesParentSession()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    h.Configuration.AutomationSession!.OceanFishingEnabled = true;
    h.Configuration.AutomationSession.OceanFishingPreparationLeadMinutes = 27;
    h.Configuration.AutomationSession.OceanFishingCharacterMode = "SelectedCharacter";
    h.Configuration.AutomationSession.OceanFishingCharacterContentId = 456;
    h.Configuration.AutomationSession.OceanFishingCharacterName = "Fishing Alt";
    h.Configuration.AutomationSession.OceanFishingCharacterWorld = "Test World";
    h.Configuration.HandledOceanFishingWindowsUtc = [new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero)];
    h.Automation.RecoverAfterReload();
    h.Farming.RecoverAfterReload();
    h.Handoff.RecoverAfterReload();
    h.Handoff.RefreshResumeReadiness();

    True(h.Farming.CanResumeAfterReload, "plan and parent session are resumable after reload");
    True(h.Handoff.CanResumeAutomationSession, "safe farming character and idle providers permit resume");
    True(h.Farming.ResumeAfterReload(h.Handoff.CanResumeAutomationSession), "coordinated resume persists");
    Equal("Active", h.Configuration.AutomationSession!.State, "parent session resumed");
    Equal("Active", h.Configuration.FarmingPlan!.State, "weekly plan resumed");
    Equal(27, h.Configuration.AutomationSession.OceanFishingPreparationLeadMinutes, "captured lead preserved");
    Equal("Fishing Alt", h.Configuration.AutomationSession.OceanFishingCharacterName, "selected fishing character preserved");
    Equal(1, h.Configuration.HandledOceanFishingWindowsUtc.Count, "handled window ledger preserved");
    Equal(0, h.Retainers.PassRequests, "resume does not replay retainer request");
}

static void TestReloadResumePersistenceFailure()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false) { FailResumeSave = true };
    h.Automation.RecoverAfterReload();
    h.Farming.RecoverAfterReload();
    h.Handoff.RecoverAfterReload();
    h.Handoff.RefreshResumeReadiness();

    True(!h.Farming.ResumeAfterReload(h.Handoff.CanResumeAutomationSession), "failed coordinated save reports failure");
    Equal("RecoveryRequired", h.Configuration.AutomationSession!.State, "parent remains blocked after save failure");
    Equal("RecoveryRequired", h.Configuration.RetainerSession!.State, "retainer remains blocked after save failure");
    Equal("RecoveryRequired", h.Configuration.FarmingPlan!.State, "plan remains blocked after save failure");
}

static void TestAmbiguousFishingBlocksResume()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    h.Configuration.OceanFishingOperation = new OceanFishingOperationRecord
    {
        OperationId = "ocean-op", SessionId = "session", State = "RecoveryRequired",
        WindowStartUtc = h.Clock.UtcNow, LaunchIntentUtc = h.Clock.UtcNow,
    };
    h.Automation.RecoverAfterReload();
    h.Farming.RecoverAfterReload();
    h.Handoff.RecoverAfterReload();
    h.Handoff.RefreshResumeReadiness();

    True(!h.Farming.CanResumeAfterReload, "ambiguous fishing ownership blocks farming resume");
    Equal(0, h.Retainers.PassRequests, "blocked recovery does not replay retainer work");
}

static void TestUninitializedTripIsIdle()
{
    Equal(false, WahToolsFeeshStatus.NormalizeTripRunning(false, null), "null trip means not running");
    Equal(null, WahToolsFeeshStatus.NormalizeTripRunning(true, null), "initialized trip with unknown running state remains unknown");
    Equal(true, WahToolsFeeshStatus.NormalizeTripRunning(true, true), "active trip remains active");
}

static void TestOceanFishingLaunchDuringWindow()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    h.Clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(7))); // 10:07 UTC, cutoff at 10:13.
    h.Configuration.AutomationSession!.OceanFishingEnabled = true;
    h.Configuration.AutomationSession.OceanFishingPreparationLeadMinutes = 15;
    var log = new NullWorkflowLog();
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = new OceanFishingHandoffController(h.Configuration, () => { }, h.Game, h.Retainers,
        h.Lifestream, feesh, () => h.Dungeon.Current is not null, () => true, log, h.Clock);

    ocean.Tick();
    Equal("VerifyingFishingCharacter", ocean.Current!.State, "farming character is accepted as selected fishing character");
    ocean.Tick();
    Equal(1, feesh.LaunchCalls, "CatchTheBoat is requested once");
    Equal("AwaitingBoarding", ocean.Current!.State, "accepted launch waits for observed boarding");
    Equal("LaunchReady", ocean.Admission!.Code, "cached admission reports passed launch preflight");
}

static void TestEarlyOceanFishingLaunchBoardsAfterOldTimeout()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    var windowStart = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    h.Clock.Advance(TimeSpan.FromMinutes(40).Add(TimeSpan.FromSeconds(20)));
    SeedAwaitingBoarding(h, windowStart, h.Clock.UtcNow);
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = CreateAwaitingBoardingController(h, feesh);

    h.Clock.Advance(TimeSpan.FromMinutes(30)); // 10:10:20, old launch-relative timeout
    ocean.Tick();
    Equal("AwaitingBoarding", ocean.Current!.State, "early launch gets registration-end boarding grace");

    h.Clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(3))); // 10:15:23, observed live boarding
    h.Game.TerritoryType = 900;
    h.Game.IsDutyStarted = true;
    h.Game.IsBoundByDuty = true;
    ocean.Tick();

    Equal("VoyageRunning", ocean.Current!.State, "actual Ocean Fishing duty is accepted after old timeout boundary");
    Equal(0, feesh.LaunchCalls, "observation does not dispatch another voyage");
}

static void TestOceanFishingBoardingWinsAtDeadline()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    var windowStart = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    h.Clock.Advance(TimeSpan.FromMinutes(40).Add(TimeSpan.FromSeconds(20)));
    SeedAwaitingBoarding(h, windowStart, h.Clock.UtcNow);
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = CreateAwaitingBoardingController(h, feesh);
    h.Clock.Advance(TimeSpan.FromMinutes(39).Add(TimeSpan.FromSeconds(40))); // 10:20:00 deadline
    h.Game.TerritoryType = 900;
    h.Game.IsBoundByDuty = true;

    ocean.Tick();

    Equal("VoyageRunning", ocean.Current!.State, "matching boarding observed on deadline tick takes precedence");
    Equal(0, feesh.LaunchCalls, "deadline observation never dispatches again");
}

static void TestOceanFishingBoardingDeadlineRecovery()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    var windowStart = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    h.Clock.Advance(TimeSpan.FromMinutes(40).Add(TimeSpan.FromSeconds(20)));
    SeedAwaitingBoarding(h, windowStart, h.Clock.UtcNow);
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = CreateAwaitingBoardingController(h, feesh);
    h.Clock.Advance(TimeSpan.FromMinutes(39).Add(TimeSpan.FromSeconds(39)));
    ocean.Tick();
    Equal("AwaitingBoarding", ocean.Current!.State, "no timeout just before 10:20 deadline");
    h.Clock.Advance(TimeSpan.FromSeconds(1));

    ocean.Tick();
    Equal("RecoveryRequired", ocean.Current!.State, "no boarding by deadline requires explicit recovery");
    True(ocean.Current.Detail.Contains("2026-10-21T10:20:00", StringComparison.Ordinal), "timeout records exact deadline");
    Equal(0, feesh.LaunchCalls, "deadline failure never retries launch");
    ocean.Tick();
    Equal(0, feesh.LaunchCalls, "recovery ticks never retry launch");
}

static void TestUnrelatedDutyDoesNotCountAsFishingBoarding()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    var windowStart = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    h.Clock.Advance(TimeSpan.FromMinutes(40).Add(TimeSpan.FromSeconds(20)));
    SeedAwaitingBoarding(h, windowStart, h.Clock.UtcNow);
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = CreateAwaitingBoardingController(h, feesh);
    h.Clock.Advance(TimeSpan.FromMinutes(39).Add(TimeSpan.FromSeconds(40)));
    h.Game.IsDutyStarted = true;
    h.Game.IsBoundByDuty = true;

    ocean.Tick();

    Equal("RecoveryRequired", ocean.Current!.State, "unrelated territory duty cannot establish fishing boarding");
    Equal(0, feesh.LaunchCalls, "unrelated duty does not trigger a relaunch");
}

static void TestOceanTerritoryWithoutDutyStillTimesOut()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    var windowStart = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    h.Clock.Advance(TimeSpan.FromMinutes(40).Add(TimeSpan.FromSeconds(20)));
    SeedAwaitingBoarding(h, windowStart, h.Clock.UtcNow);
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = CreateAwaitingBoardingController(h, feesh);
    h.Clock.Advance(TimeSpan.FromMinutes(39).Add(TimeSpan.FromSeconds(40)));
    h.Game.TerritoryType = 900;

    ocean.Tick();

    Equal("RecoveryRequired", ocean.Current!.State, "territory entry alone cannot suppress boarding timeout");
    Equal(0, feesh.LaunchCalls, "territory-only observation never retries launch");
}

static void SeedAwaitingBoarding(Harness h, DateTimeOffset windowStart, DateTimeOffset launchIntentUtc)
{
    h.Configuration.OceanFishingOperation = new OceanFishingOperationRecord
    {
        OperationId = "awaiting-boarding", SessionId = "session", State = "AwaitingBoarding",
        WindowStartUtc = windowStart, LaunchIntentUtc = launchIntentUtc, PhaseChangedUtc = launchIntentUtc,
        FarmingCharacterContentId = 123, FarmingCharacterName = "Farm Main", FarmingCharacterWorld = "Test World",
        FishingCharacterContentId = 123, FishingCharacterName = "Farm Main", FishingCharacterWorld = "Test World",
    };
}

static OceanFishingHandoffController CreateAwaitingBoardingController(Harness h, FakeFeesh feesh) =>
    new(h.Configuration, () => { }, h.Game, h.Retainers, h.Lifestream, feesh,
        () => false, () => true, new NullWorkflowLog(), h.Clock);

static void TestOceanFishingMissedCutoff()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    h.Configuration.AutomationSession!.OceanFishingEnabled = true;
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = new OceanFishingHandoffController(h.Configuration, () => { }, h.Game, h.Retainers,
        h.Lifestream, feesh, () => true, () => true, new NullWorkflowLog(), h.Clock);

    h.Clock.Advance(TimeSpan.FromMinutes(45)); // observed preparation for 10:00 UTC window
    ocean.Tick();
    h.Clock.Advance(TimeSpan.FromMinutes(28)); // 10:13 UTC launch cutoff
    ocean.Tick();
    Equal(0, feesh.LaunchCalls, "cutoff prevents CatchTheBoat");
    Equal("MissedLaunchCutoff", ocean.Admission!.Code, "missed cutoff is visible");
    True(h.Configuration.HandledOceanFishingWindowsUtc.Contains(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero)),
        "missed window is persisted as handled");
}

static void TestFishingPriorityDefersRetainerCheckpoint()
{
    var h = new Harness(known: true, ready: true, seedCompletedRun: false);
    h.Configuration.FarmingPlan!.RetainerCheckpointOperationId = "verified-run";

    h.Handoff.Tick(allowNewWork: false);
    Equal(0, h.Retainers.PassRequests, "fishing priority prevents a new checkpoint-triggered pass");
    Equal("verified-run", h.Configuration.FarmingPlan.RetainerCheckpointOperationId, "checkpoint is preserved while deferred");
    True(h.Configuration.RetainerHandoff is null, "no handoff or launch intent is created while deferred");

    h.Handoff.Tick(allowNewWork: true);
    Equal(1, h.Retainers.PassRequests, "deferred checkpoint proceeds when priority is released");
    Equal("AwaitingStart", h.Configuration.RetainerHandoff!.State, "new pass retains normal ownership lifecycle");

    h.Retainers.Busy = true;
    h.Retainers.Active = true;
    h.Handoff.Tick(allowNewWork: false);
    Equal("RunningPass", h.Configuration.RetainerHandoff.State,
        "an already-owned pass is still observed and allowed to settle while new work is paused");
    Equal(1, h.Retainers.PassRequests, "settling owned work does not issue a second pass");
}

static void TestUnobservedFishingWindowIsNotMissed()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    h.Configuration.AutomationSession!.OceanFishingEnabled = true;
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = new OceanFishingHandoffController(h.Configuration, () => { }, h.Game, h.Retainers,
        h.Lifestream, feesh, () => false, () => true, new NullWorkflowLog(), h.Clock);

    h.Clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(14))); // started after cutoff, with no prior observation
    ocean.Tick();

    Equal(0, feesh.LaunchCalls, "late start does not launch into an expired window");
    True(!h.Configuration.HandledOceanFishingWindowsUtc.Contains(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero)),
        "unobserved historical window is not marked handled");
    True(ocean.Admission!.Code != "MissedLaunchCutoff", "no retrospective missed-window diagnostic");
}

static void TestObservedFishingWindowMissedOnce()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    h.Configuration.AutomationSession!.OceanFishingEnabled = true;
    var log = new RecordingWorkflowLog();
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = new OceanFishingHandoffController(h.Configuration, () => { }, h.Game, h.Retainers,
        h.Lifestream, feesh, () => true, () => true, log, h.Clock);

    h.Clock.Advance(TimeSpan.FromMinutes(45));
    ocean.Tick();
    h.Clock.Advance(TimeSpan.FromMinutes(28));
    ocean.Tick();
    Equal("MissedLaunchCutoff", ocean.Admission!.Code, "actively observed window reports its missed cutoff");
    Equal(1, log.WarningMessages.Count(message => message.Contains("MissedLaunchCutoff", StringComparison.Ordinal)),
        "missed cutoff logs once");
    ocean.Tick();
    Equal(1, log.WarningMessages.Count(message => message.Contains("MissedLaunchCutoff", StringComparison.Ordinal)),
        "subsequent ticks do not repeat missed cutoff warning");
}

static void TestFishingWaitDiagnosticsAreInformational()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    var log = new RecordingWorkflowLog();
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var ocean = new OceanFishingHandoffController(h.Configuration, () => { }, h.Game, h.Retainers,
        h.Lifestream, feesh, () => false, () => true, log, h.Clock);

    ocean.Tick();
    True(log.InformationMessages.Any(message => message.Contains("SessionInactive", StringComparison.Ordinal)),
        "inactive fishing session is informational");
    Equal(0, log.WarningMessages.Count, "inactive fishing session is not a warning");

    h.Configuration.AutomationSession!.OceanFishingEnabled = true;
    h.Clock.Advance(TimeSpan.FromMinutes(45));
    using var ownedOcean = new OceanFishingHandoffController(h.Configuration, () => { }, h.Game, h.Retainers,
        h.Lifestream, feesh, () => true, () => true, log, h.Clock);
    ownedOcean.Tick();
    True(log.InformationMessages.Any(message => message.Contains("DungeonOwnsActivity", StringComparison.Ordinal)),
        "owned dungeon wait is informational");
    Equal(0, log.WarningMessages.Count, "normal dungeon ownership wait is not a warning");

    using var retainerOwnedOcean = new OceanFishingHandoffController(h.Configuration, () => { }, h.Game, h.Retainers,
        h.Lifestream, feesh, () => false, () => true, log, h.Clock, () => true);
    retainerOwnedOcean.Tick();
    Equal("RetainerOwnsActivity", retainerOwnedOcean.Admission!.Code, "known retainer ownership uses a stable blocker");
    True(log.InformationMessages.Any(message => message.Contains("RetainerOwnsActivity", StringComparison.Ordinal)),
        "owned retainer wait is informational");
    Equal(0, log.WarningMessages.Count, "normal retainer ownership wait is not a warning");
}

static void TestReloadRecoveryCanStop()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    h.Automation.RecoverAfterReload();
    h.Farming.RecoverAfterReload();
    h.Handoff.RecoverAfterReload();
    h.Handoff.RefreshResumeReadiness();

    True(h.Farming.AcknowledgeRecoveryAndStop(h.Handoff.CanResumeAutomationSession), "safe recovery stop is accepted independently");
    Equal("RecoveryAcknowledged", h.Configuration.FarmingPlan!.State, "plan stop is acknowledged");
    Equal("Stopped", h.Configuration.AutomationSession!.State, "parent automation is stopped");
    Equal("Stopped", h.Configuration.RetainerSession!.State, "linked retainer session is stopped");
}

static void TestRecoveryStopPreservesFishingOwnership()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    h.Configuration.OceanFishingOperation = new OceanFishingOperationRecord
    {
        OperationId = "owned-ocean", SessionId = "session", State = "RecoveryRequired",
        WindowStartUtc = h.Clock.UtcNow, LaunchIntentUtc = h.Clock.UtcNow,
    };
    h.Automation.RecoverAfterReload();
    h.Farming.RecoverAfterReload();
    h.Handoff.RecoverAfterReload();

    True(!h.Farming.AcknowledgeRecoveryAndStop(true), "stop refuses to erase unresolved fishing ownership");
    Equal("RecoveryRequired", h.Configuration.FarmingPlan!.State, "plan stays in recovery");
    Equal("RecoveryRequired", h.Configuration.OceanFishingOperation!.State, "fishing operation is preserved");
}

static void TestFishingRecoveryAcknowledgedSafely()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    SeedFishingRecovery(h);
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var controller = CreateFishingRecoveryController(h, feesh);

    controller.Tick();
    True(controller.CanAcknowledgeRecovery, "safe farming character and idle providers enable acknowledgment");
    True(controller.RequestRecoveryAcknowledgement(), "user request queues acknowledgment");
    Equal("RecoveryRequired", h.Configuration.OceanFishingOperation!.State, "request alone does not mutate recovery state");
    controller.Tick();

    Equal("RecoveryAcknowledged", h.Configuration.OceanFishingOperation!.State, "recovery is acknowledged on framework tick");
    True(h.Configuration.HandledOceanFishingWindowsUtc!.Contains(h.Configuration.OceanFishingOperation.WindowStartUtc),
        "ambiguous window remains handled");
    Equal("Stopped", h.Configuration.AutomationSession!.State, "linked parent automation stopped");
    Equal("Stopped", h.Configuration.FarmingPlan!.State, "linked farming plan stopped");
    Equal("Stopped", h.Configuration.RetainerSession!.State, "linked retainer session stopped");
    Equal(0, feesh.LaunchCalls, "acknowledgment never launches a voyage");
    Equal("RecoveryAcknowledged", controller.Admission!.Code, "stale LaunchReady admission replaced");
}

static void TestFishingRecoveryAcknowledgementRevalidates()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    SeedFishingRecovery(h);
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var controller = CreateFishingRecoveryController(h, feesh);

    controller.Tick();
    True(controller.CanAcknowledgeRecovery, "initial safe readiness cached");
    h.Game.Name = "Different Character";
    True(controller.RequestRecoveryAcknowledgement(), "acknowledgment queued from cached-ready UI");
    controller.Tick();
    Equal("RecoveryRequired", h.Configuration.OceanFishingOperation!.State, "character change blocks acknowledgment");
    True(controller.RecoveryAcknowledgementStatus.Contains("Return to the farming character", StringComparison.Ordinal),
        "character blocker is visible");

    h.Game.Name = "Farm Main";
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    controller.Tick();
    True(controller.CanAcknowledgeRecovery, "readiness returns after farming character verified");
    h.Game.IsBoundByDuty = true;
    True(controller.RequestRecoveryAcknowledgement(), "second request queued");
    controller.Tick();
    Equal("RecoveryRequired", h.Configuration.OceanFishingOperation!.State, "active duty blocks acknowledgment");
    True(controller.RecoveryAcknowledgementStatus.Contains("active duty", StringComparison.Ordinal),
        "duty blocker is visible");
    Equal(0, feesh.LaunchCalls, "revalidation never relaunches voyage");
}

static void TestFishingRecoveryAcknowledgementSaveFailure()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    SeedFishingRecovery(h);
    var failSave = false;
    var feesh = new FakeFeesh(() => CreateIdleFeeshStatus(h.Clock.UtcNow));
    using var controller = CreateFishingRecoveryController(h, feesh, () =>
    {
        if (failSave)
            throw new IOException("Injected recovery acknowledgment persistence failure.");
    });
    controller.Tick();
    True(controller.RequestRecoveryAcknowledgement(), "safe request queued");
    failSave = true;
    controller.Tick();

    Equal("RecoveryRequired", h.Configuration.OceanFishingOperation!.State, "failed persistence keeps ownership unresolved");
    Equal("PersistenceFailure", h.Configuration.OceanFishingOperation.RecoveryKind, "persistence failure is explicit");
    True(controller.NeedsRecovery, "recovery remains visible");
    Equal(0, feesh.LaunchCalls, "save failure never launches voyage");
}

static void SeedFishingRecovery(Harness h)
{
    h.Configuration.AutomationSession = new AutomationSessionRecord
    {
        SessionId = "session", State = "RecoveryRequired", RecoveryKind = "OceanFishing",
        FarmingPlanId = "plan", FarmingCharacterContentId = 123,
        FarmingCharacterName = "Farm Main", FarmingCharacterWorld = "Test World",
    };
    h.Configuration.FarmingPlan!.State = "Active";
    h.Configuration.FarmingPlan.StopRequested = false;
    h.Configuration.RetainerSession!.State = "Active";
    h.Configuration.RetainerSession.StopRequested = false;
    h.Configuration.OceanFishingOperation = new OceanFishingOperationRecord
    {
        OperationId = "ambiguous-op", SessionId = "session", State = "RecoveryRequired",
        RecoveryKind = "AmbiguousProviderOutcome", WindowStartUtc = h.Clock.UtcNow.AddHours(-1),
        LaunchIntentUtc = h.Clock.UtcNow.AddHours(-1), FarmingCharacterContentId = 123,
        FarmingCharacterName = "Farm Main", FarmingCharacterWorld = "Test World",
        FishingCharacterContentId = 456, FishingCharacterName = "Fishing Alt", FishingCharacterWorld = "Test World",
        Detail = "Ambiguous launch; no replay.",
    };
}

static OceanFishingHandoffController CreateFishingRecoveryController(Harness h, FakeFeesh feesh, Action? save = null) =>
    new(h.Configuration, save ?? (() => { }), h.Game, h.Retainers, h.Lifestream, feesh,
        () => false, () => true, new NullWorkflowLog(), h.Clock, () => false);

static void TestAcknowledgedRecoveryAllowsNewPlan()
{
    var h = new Harness(known: true, ready: false, seedCompletedRun: false);
    var plan = h.Configuration.FarmingPlan!;
    plan.State = "RecoveryAcknowledged";
    plan.RecoveryKind = "AcknowledgedStop";
    plan.StopRequested = true;

    True(h.Farming.CanStart, "acknowledged plan can be replaced before weekly reset");
}

static WahToolsFeeshStatus CreateIdleFeeshStatus(DateTimeOffset now) => new(
    true, now, "WahTools Feesh idle",
    WahToolsFeeshStatus.NormalizeTripRunning(false, null), false, false, false, false, false, false,
    "Idle", "Trip not initialized", "Idle", 1)
{
    TripInitialized = false,
};

static void TestVentureOutsideWait()
{
    var h = new Harness(known: true, ready: false);
    h.Retainers.ClosestEligibleSeconds = 301;
    h.TickAll();
    Equal(null, h.Configuration.FarmingPlan!.RetainerCheckpointOperationId, "checkpoint clears beyond threshold");
    Equal(0, h.Retainers.PassRequests, "not-ready venture does not trigger AR pass");
    h.TickAll();
    True(h.Dungeon.Current is { State: "PendingValidation" }, "dungeon queues after checkpoint clears");
}

static void CompleteHandoff(Harness h)
{
    h.TickAll();
    h.Retainers.Busy = true;
    h.Retainers.Active = true;
    h.TickAll();
    h.Game.Name = "Venture Alt";
    h.Retainers.Busy = false;
    h.Retainers.Active = false;
    h.Retainers.FinishPass();
    h.TickAll();
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
    h.TickAll();
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    h.TickAll();
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
}

sealed class Harness
{
    private static WahToolsFeeshStatus CreateIdleStatus(DateTimeOffset now) => new(
        true, now, "WahTools Feesh idle",
        WahToolsFeeshStatus.NormalizeTripRunning(false, null), false, false, false, false, false, false,
        "Idle", "Trip not initialized", "Idle", 1)
    {
        TripInitialized = false,
    };

    public readonly TestClock Clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    public readonly Configuration Configuration;
    public readonly FakeGame Game = new();
    public readonly FakeAutoDuty AutoDuty = new();
    public readonly FakeRetainers Retainers;
    public readonly FakeLifestream Lifestream;
    public readonly DungeonRunController Dungeon;
    public readonly FarmingPlanController Farming;
    public readonly RetainerHandoffController Handoff;
    public readonly AutomationSessionController Automation;
    public readonly FakeFeesh Feesh;
    public readonly OceanFishingHandoffController Ocean;
    public readonly ManualStopController ManualStop;
    public bool FailCheckpointClearSave { get; set; }
    public bool FailResumeSave { get; set; }
    public int WeeklyAcquired { get; set; } = 100;
    public string EligibilityStatus { get; set; } = "Eligible";
    public int EligibilityChecks { get; private set; }
    private bool failedCheckpointSave;
    private string? lastSavedConfiguration;

    public Harness(bool known, bool ready, Configuration? restoredConfiguration = null, bool seedCompletedRun = true)
    {
        Configuration = restoredConfiguration ?? new Configuration { MonitorRetainerVentures = true };
        Retainers = new FakeRetainers { Known = known, Ready = ready, Clock = Clock };
        Lifestream = new FakeLifestream(Game);
        Feesh = new FakeFeesh(() => CreateIdleStatus(Clock.UtcNow));
        var log = new NullWorkflowLog();
        Automation = new AutomationSessionController(Configuration, Save, log, Clock);
        void Save()
        {
            if (FailResumeSave && Configuration.AutomationSession is { State: "Active" } &&
                Configuration.FarmingPlan is { State: "Active" })
                throw new IOException("Injected coordinated resume persistence failure.");
            if (FailCheckpointClearSave && !failedCheckpointSave &&
                Configuration.RetainerHandoff is { State: "Completed" } &&
                Configuration.FarmingPlan is { RetainerCheckpointOperationId: null })
            {
                failedCheckpointSave = true;
                throw new IOException("Injected checkpoint persistence failure.");
            }
            lastSavedConfiguration = JsonSerializer.Serialize(Configuration);
        }
        Dungeon = new DungeonRunController(Configuration, Save, AutoDuty, Retainers, Game, log,
            CheckEligibility,
            () => new DebugTests.LiveTomeState(123, 49, WeeklyAcquired, 900, 100), Clock);
        Farming = new FarmingPlanController(Configuration, Save, Dungeon, Game, Automation, log,
            () => new DebugTests.LiveTomeState(123, 49, WeeklyAcquired, 900, 100),
            CheckEligibility,
            territory => new DebugTests.DungeonOption(territory, 456, 789, "Test Dungeon", 50, 1, 1, true, true),
            Clock);
        Handoff = new RetainerHandoffController(Configuration, Save, Retainers, Lifestream, Game,
            Dungeon, Farming, () => Retainers.ReadReadiness(123), () => Retainers.ReadReadiness(123),
            () => true, log, Clock);
        Handoff.Subscribe();
        Ocean = new OceanFishingHandoffController(Configuration, Save, Game, Retainers, Lifestream, Feesh,
            () => Dungeon.Current is not null, () => AutoDuty.Stopped, log, Clock, () => Handoff.OwnsActivity);
        ManualStop = new ManualStopController(Configuration, Save, Automation, Farming, Dungeon, Handoff, Ocean,
            () => AutoDuty.Stopped, log, Clock);

        if (Configuration.FarmingPlan is null)
        {
            var week = FarmingSchedulePolicy.GetWeekStartUtc(Clock.UtcNow);
            var period = FarmingSchedulePolicy.GetPeriodStartUtc(Clock.UtcNow, week);
            Configuration.FarmingPlan = new FarmingPlanRecord
            {
                PlanId = "plan", State = "Active", CharacterContentId = 123, TerritoryId = 321,
                FarmingCharacterName = Game.Name, FarmingCharacterWorld = Game.World,
                ContentFinderConditionId = 456, DungeonName = "Test Dungeon", Mode = "Support",
                GearsetInspectionId = 0, BareMode = true, ServiceRetainers = true, DungeonReward = 50,
                CurrencyItemId = 49, WeeklyLimitAtStart = 900, WeeklyResetUtc = week.AddDays(7),
                WeekStartUtc = week, FinishDeadlineUtc = week.AddDays(7), CurrentPeriodStartUtc = period,
                RequestedPeriodCount = 7, CurrentOperationId = seedCompletedRun ? "run-1" : null,
                Periods = [new FarmingPeriodRecord { StartUtc = period, AllocatedRuns = 2 }],
            };
            if (seedCompletedRun)
            {
                Configuration.DungeonRun = new DungeonRunRecord
                {
                    OperationId = "run-1", FarmingPlanId = "plan", FarmingPeriodStartUtc = period,
                    State = "Completed", RewardOutcome = "Verified", CharacterContentId = 123,
                    ExpectedReward = 50, WeeklyAcquiredBefore = 50, WeeklyAcquiredAfter = 100,
                    ObservedWeeklyDelta = 50, CurrencyItemId = 49, WeeklyLimitAtRequest = 900,
                };
            }
            Configuration.RetainerSession = new RetainerAutomationSessionRecord
            {
                SessionId = "session", FarmingPlanId = "plan", State = "Active", ServiceRetainers = true,
                FarmingCharacterContentId = 123, FarmingCharacterName = Game.Name, FarmingCharacterWorld = Game.World,
                ExpiresUtc = week.AddDays(7),
            };
            Configuration.AutomationSession = new AutomationSessionRecord
            {
                SessionId = "session", State = "Active", FarmingPlanId = "plan",
                FarmingCharacterContentId = 123, FarmingCharacterName = Game.Name, FarmingCharacterWorld = Game.World,
                ServiceRetainers = true, RetainerWaitBeforeDungeonMinutes = 5,
                CreatedUtc = Clock.UtcNow, OceanFishingCharacterMode = "CurrentCharacter",
            };
            if (seedCompletedRun)
                Dungeon.Tick(); // mirrors the prior production tick that published CanAcknowledge for the verified run.
        }
    }

    private DebugReport CheckEligibility(uint _, DebugTests.DutyModeSelection __, int ___)
    {
        EligibilityChecks++;
        return new DebugReport(EligibilityStatus, string.Empty);
    }

    public void TickAll()
    {
        if (ManualStop.Tick())
            return;
        Farming.Tick(Handoff.OwnsActivity);
        Handoff.Tick();
        Dungeon.Tick();
    }

    public string Snapshot() => lastSavedConfiguration ?? JsonSerializer.Serialize(Configuration);
    public void PersistSnapshot() => lastSavedConfiguration = JsonSerializer.Serialize(Configuration);
}

sealed class TestClock(DateTimeOffset now) : IUtcClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Advance(TimeSpan amount) => UtcNow += amount;
}

sealed class FakeGame : IWorkflowGameState
{
    public ulong ContentId { get; set; } = 123;
    public bool IsLoggedIn { get; set; } = true;
    public bool HasLocalPlayer { get; set; } = true;
    public bool IsLocalPlayerAlive { get; set; } = true;
    public uint TerritoryType { get; set; } = 100;
    public uint ContentFinderConditionId { get; set; }
    public bool IsDutyStarted { get; set; }
    public bool IsBoundByDuty { get; set; }
    public bool InCombat { get; set; }
    public bool WaitingForDutyFinder { get; set; }
    public bool BetweenAreas { get; set; }
    public bool BetweenAreas51 { get; set; }
    public bool Occupied { get; set; }
    public bool IsSafePlayerState => IsLoggedIn && HasLocalPlayer && IsLocalPlayerAlive && !IsDutyStarted && !IsBoundByDuty &&
        !InCombat && !WaitingForDutyFinder && !BetweenAreas && !BetweenAreas51;
    public string Name { get; set; } = "Farm Main";
    public string World { get; set; } = "Test World";
    public string CharacterName => Name;
    public string HomeWorld => World;
    public event Action? DutyStarted;
    public event Action? DutyCompleted;
    public void RaiseDutyStarted() { IsDutyStarted = true; DutyStarted?.Invoke(); }
    public void RaiseDutyCompleted() => DutyCompleted?.Invoke();
}

sealed class FakeAutoDuty : IAutoDutyClient
{
    public int RunRequests { get; private set; }
    public bool Stopped { get; set; } = true;
    public string DutyMode { get; set; } = "Trust";
    public int ConfiguredLoopCount { get; set; } = 5;
    public int RunLoopCount { get; private set; }
    public int ModeWriteRequests { get; private set; }
    public int ModeWriteFailures { get; set; }
    public bool IgnoreModeWrites { get; set; }
    public bool IsStopped() => Stopped;
    public string GetConfig(string name) => name switch
    {
        "Meta.AutoDutyModeEnum" => "Looping",
        "Meta.LoopTimes" => ConfiguredLoopCount.ToString(),
        "Meta.DutyModeEnum" => DutyMode,
        _ => string.Empty,
    };
    public void SetConfig(string name, string value)
    {
        if (name != "Meta.DutyModeEnum")
            throw new InvalidOperationException($"Unexpected AutoDuty config write: {name}");
        ModeWriteRequests++;
        if (ModeWriteFailures > 0)
        {
            ModeWriteFailures--;
            throw new IOException("injected mode write failure");
        }
        if (!IgnoreModeWrites)
            DutyMode = value;
    }
    public bool ContentHasPath(uint territoryId) => true;
    public void Run(uint territoryId, int loops, bool bareMode)
    {
        RunRequests++;
        RunLoopCount = loops;
        ConfiguredLoopCount = loops;
        Stopped = false;
    }
}

sealed class FakeRetainers : IRetainerProvider
{
    public bool Known { get; set; }
    public bool Ready { get; set; }
    public bool Busy { get; set; }
    public bool Active { get; set; }
    public TestClock Clock { get; set; } = null!;
    public long? ClosestEligibleSeconds { get; set; }
    public bool FinishSynchronously { get; set; }
    public int PassRequests { get; private set; }
    public int CancelRequests { get; private set; }
    public bool CancelAccepted { get; set; } = true;
    public event Action? SinglePassFinished;
    public bool TryGetProviderState(out bool busy, out bool multiModeActive)
    {
        busy = Busy; multiModeActive = Active; return Known;
    }
    public RetainerReadinessSnapshot ReadReadiness(ulong currentCharacterContentId = 0) =>
        new(Known, Ready, 4, Ready ? 1 : 0, ClosestEligibleSeconds, Busy, Active, Clock.UtcNow,
            Known ? null : "test unknown readiness", "ready-fingerprint");
    public bool TryGetRegisteredCharacters(out IReadOnlyList<RetainerCharacterIdentity> characters)
    {
        characters = [new RetainerCharacterIdentity(123, "Farm Main", "Test World")];
        return Known;
    }
    public void RequestSingleRetainerPass()
    {
        PassRequests++;
        Busy = !FinishSynchronously;
        Active = !FinishSynchronously;
        if (FinishSynchronously) SinglePassFinished?.Invoke();
    }
    public bool TryCancelOwnedPass(out string detail)
    {
        CancelRequests++;
        if (CancelAccepted)
        {
            Busy = false;
            Active = false;
            detail = "cancelled";
            return true;
        }
        detail = "injected cancellation failure";
        return false;
    }
    public void FinishPass() => SinglePassFinished?.Invoke();
}

sealed class FakeLifestream(FakeGame game) : ILifestreamProvider
{
    public int ReturnRequests { get; private set; }
    public int AbortRequests { get; private set; }
    public bool Busy { get; set; }
    public bool TryGetBusy(out bool busy) { busy = Busy; return true; }
    public bool TryChangeCharacter(string name, string world, out string result)
    {
        ReturnRequests++;
        game.Name = name;
        game.World = world;
        result = "Success";
        return true;
    }
    public bool TryAbortOwnedRequest(out string result)
    {
        AbortRequests++;
        Busy = false;
        result = "aborted";
        return true;
    }
}

sealed class FakeFeesh(Func<WahToolsFeeshStatus> readStatus) : IWahToolsFeeshClient
{
    public bool IsAvailable => true;
    public int LaunchCalls { get; private set; }
    public int StopCalls { get; private set; }
    public WahToolsFeeshStatus ReadStatus() => readStatus();
    public FeeshRequestResult RequestSingleVoyage(out string detail)
    {
        LaunchCalls++;
        detail = "Accepted";
        return FeeshRequestResult.Accepted;
    }
    public FeeshRequestResult Stop(out string detail)
    {
        StopCalls++;
        detail = "Stopped";
        return FeeshRequestResult.Accepted;
    }
}

sealed class NullWorkflowLog : IWorkflowLog
{
    public void Information(string message, params object[] values) { }
    public void Warning(string message, params object[] values) { }
    public void Warning(Exception exception, string message, params object[] values) { }
    public void Error(string message, params object[] values) { }
    public void Error(Exception exception, string message, params object[] values) { }
}

sealed class RecordingWorkflowLog : IWorkflowLog
{
    public List<string> InformationMessages { get; } = [];
    public List<string> WarningMessages { get; } = [];
    public void Information(string message, params object[] values) => InformationMessages.Add(Format(message, values));
    public void Warning(string message, params object[] values) => WarningMessages.Add(Format(message, values));
    public void Warning(Exception exception, string message, params object[] values) => WarningMessages.Add(Format(message, values));
    public void Error(string message, params object[] values) { }
    public void Error(Exception exception, string message, params object[] values) { }
    private static string Format(string message, object[] values) => $"{message} {string.Join(" ", values)}";
}
