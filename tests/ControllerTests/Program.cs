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
    ("Stop during near-complete wait prevents new provider work", TestStopDuringVentureWait),
    ("unsafe farming-character return verification times out without retry", TestReturnVerificationTimeout),
    ("near-complete wait services venture when it becomes ready", TestVentureBecomesReady),
    ("venture beyond wait window permits continuation without an AR pass", TestVentureOutsideWait),
    ("disabled service clears checkpoint without requesting AutoRetainer", TestServiceDisabled),
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

static void TestStopDuringVentureWait()
{
    var h = new Harness(known: true, ready: false);
    h.Retainers.ClosestEligibleSeconds = 300;
    h.TickAll();
    Equal(0, h.Retainers.PassRequests, "near-complete venture waits without an early pass");
    h.Handoff.RequestStop();
    h.TickAll();
    Equal(0, h.Retainers.PassRequests, "Stop prevents pass request");
    Equal(0, h.AutoDuty.RunRequests, "Stop prevents dungeon request");
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
    h.Configuration.MonitorRetainerVentures = false;
    h.TickAll();
    Equal(0, h.Retainers.PassRequests, "disabled setting prevents AutoRetainer request");
    Equal(null, h.Configuration.FarmingPlan!.RetainerCheckpointOperationId, "disabled service clears checkpoint");
    Equal(0, h.AutoDuty.RunRequests, "dungeon is queued only on subsequent tick");
}

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
    public readonly TestClock Clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    public readonly Configuration Configuration;
    public readonly FakeGame Game = new();
    public readonly FakeAutoDuty AutoDuty = new();
    public readonly FakeRetainers Retainers;
    public readonly FakeLifestream Lifestream;
    public readonly DungeonRunController Dungeon;
    public readonly FarmingPlanController Farming;
    public readonly RetainerHandoffController Handoff;
    public bool FailCheckpointClearSave { get; set; }
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
        var log = new NullWorkflowLog();
        void Save()
        {
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
            () => new DebugTests.LiveTomeState(123, 49, WeeklyAcquired, 900, 100));
        Farming = new FarmingPlanController(Configuration, Save, Dungeon, Game, log,
            () => new DebugTests.LiveTomeState(123, 49, WeeklyAcquired, 900, 100),
            CheckEligibility,
            territory => new DebugTests.DungeonOption(territory, 456, 789, "Test Dungeon", 50, 1, 1, true, true),
            Clock);
        Handoff = new RetainerHandoffController(Configuration, Save, Retainers, Lifestream, Game,
            Dungeon, Farming, () => Retainers.ReadReadiness(123), () => Retainers.ReadReadiness(123),
            () => true, log, Clock);
        Handoff.Subscribe();

        if (Configuration.FarmingPlan is null)
        {
            var week = FarmingSchedulePolicy.GetWeekStartUtc(Clock.UtcNow);
            var period = FarmingSchedulePolicy.GetPeriodStartUtc(Clock.UtcNow, week);
            Configuration.FarmingPlan = new FarmingPlanRecord
            {
                PlanId = "plan", State = "Active", CharacterContentId = 123, TerritoryId = 321,
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
    public bool IsStopped() => Stopped;
    public string GetConfig(string name) => name switch
    {
        "Meta.AutoDutyModeEnum" => "Looping",
        "Meta.LoopTimes" => "1",
        "Meta.DutyModeEnum" => "Support",
        _ => string.Empty,
    };
    public bool ContentHasPath(uint territoryId) => true;
    public void Run(uint territoryId, bool bareMode) { RunRequests++; Stopped = false; }
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
    public event Action? SinglePassFinished;
    public bool TryGetProviderState(out bool busy, out bool multiModeActive)
    {
        busy = Busy; multiModeActive = Active; return Known;
    }
    public RetainerReadinessSnapshot ReadReadiness(ulong currentCharacterContentId = 0) =>
        new(Known, Ready, 4, Ready ? 1 : 0, ClosestEligibleSeconds, Busy, Active, Clock.UtcNow,
            Known ? null : "test unknown readiness", "ready-fingerprint");
    public void RequestSingleRetainerPass()
    {
        PassRequests++;
        Busy = !FinishSynchronously;
        Active = !FinishSynchronously;
        if (FinishSynchronously) SinglePassFinished?.Invoke();
    }
    public void FinishPass() => SinglePassFinished?.Invoke();
}

sealed class FakeLifestream(FakeGame game) : ILifestreamProvider
{
    public int ReturnRequests { get; private set; }
    public bool TryGetBusy(out bool busy) { busy = false; return true; }
    public bool TryChangeCharacter(string name, string world, out string result)
    {
        ReturnRequests++;
        game.Name = name;
        game.World = world;
        result = "Success";
        return true;
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
