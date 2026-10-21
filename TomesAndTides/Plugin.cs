using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using TomesAndTides.Debugging;
using TomesAndTides.Windows;
using TomesAndTides.Integrations;

namespace TomesAndTides;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager Commands { get; set; } = null!;
    [PluginService] private static IDataManager Data { get; set; } = null!;
    [PluginService] private static IClientState Client { get; set; } = null!;
    [PluginService] private static IObjectTable Objects { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IUnlockState Unlocks { get; set; } = null!;
    [PluginService] private static IPlayerState PlayerState { get; set; } = null!;
    [PluginService] private static ICondition Conditions { get; set; } = null!;
    [PluginService] private static IDutyState DutyState { get; set; } = null!;

    private const string MainCommand = "/tomestides";
    private const string Alias = "/tt";
    private readonly WindowSystem windows = new("TomesAndTides");
    private readonly DebugSession session;
    private readonly MainWindow mainWindow;
    private readonly DebugWindow debugWindow;
    private readonly Configuration configuration;
    private readonly DungeonRunController dungeonRun;
    private readonly FarmingPlanController farmingPlan;
    private readonly AutomationSessionController automationSession;
    private readonly AutoRetainerClient autoRetainer;
    private readonly AutoDutyClient autoDuty;
    private readonly DalamudWorkflowGameState gameState;
    private readonly RetainerHandoffController retainerHandoff;
    private readonly OceanFishingHandoffController oceanFishing;
    private RetainerReadinessSnapshot? retainerReadiness;
    private IReadOnlyList<RetainerCharacterIdentity> retainerRoster = [];
    private bool retainerRosterKnown;
    private DateTimeOffset nextRetainerScanUtc;
    private DateTimeOffset nextRosterScanUtc;
    private readonly bool aliasRegistered;
    private DateTimeOffset nextDashboardRefreshUtc;
    private readonly IWorkflowLog workflowLog;

    public Plugin()
    {
        var loadedUtc = DateTimeOffset.UtcNow;
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        workflowLog = new DalamudWorkflowLog(Log);
        autoRetainer = new AutoRetainerClient(PluginInterface, Log);
        autoDuty = new AutoDutyClient(PluginInterface);
        gameState = new DalamudWorkflowGameState(Client, Objects, PlayerState, Conditions, DutyState);
        automationSession = new AutomationSessionController(configuration, SaveConfiguration, workflowLog);
        var debugTests = new DebugTests(Data, Client, Objects, PluginInterface, Unlocks, PlayerState, Conditions, configuration);
        session = new DebugSession(debugTests, Log);
        dungeonRun = new DungeonRunController(configuration, SaveConfiguration, autoDuty, autoRetainer,
            gameState, workflowLog, debugTests.CheckEligibility,
            debugTests.ReadLiveTomeState);
        farmingPlan = new FarmingPlanController(configuration, SaveConfiguration, dungeonRun, gameState, automationSession, workflowLog,
            debugTests.ReadLiveTomeState, debugTests.CheckEligibility,
            territory => session.DungeonChoices.FirstOrDefault(choice => choice.Territory == territory));
        var lifestream = new LifestreamClient(PluginInterface);
        retainerHandoff = new RetainerHandoffController(configuration, SaveConfiguration, autoRetainer, lifestream,
            gameState, dungeonRun, farmingPlan,
            () => retainerReadiness, RefreshRetainerReadiness, debugTests.ReadAutoDutyStopped, workflowLog);
        var feesh = new WahToolsFeeshClient(PluginInterface, new WahToolsFeeshStatusReader(workflowLog), workflowLog);
        oceanFishing = new OceanFishingHandoffController(configuration, SaveConfiguration, gameState,
            autoRetainer, lifestream, feesh, () => dungeonRun.Current is not null,
            debugTests.ReadAutoDutyStopped, workflowLog);
        retainerHandoff.Subscribe();
        automationSession.RecoverAfterReload();
        farmingPlan.RecoverAfterReload();
        retainerHandoff.RecoverAfterReload();
        oceanFishing.RecoverAfterReload();
        mainWindow = new MainWindow(session, configuration, dungeonRun, farmingPlan, SaveConfiguration, OpenDiagnostics, Log,
            () => retainerReadiness, retainerHandoff, automationSession, oceanFishing,
            () => (retainerRosterKnown, retainerRoster));
        debugWindow = new DebugWindow(session, configuration, dungeonRun, farmingPlan, retainerHandoff,
            SaveConfiguration, Log, OpenMain);
        if (!Commands.AddHandler(MainCommand, new CommandInfo(OnCommand)
            { HelpMessage = "Open the Tomes & Tides main window." }))
            throw new InvalidOperationException($"Command {MainCommand} is already registered.");

        aliasRegistered = Commands.AddHandler(Alias, new CommandInfo(OnCommand)
            { HelpMessage = $"Alias for {MainCommand}.", ShowInHelp = false });
        if (!aliasRegistered)
            Log.Warning("/tt is already registered; use /tomestides instead.");
        mainWindow.AliasAvailable = aliasRegistered;
        debugWindow.AliasAvailable = aliasRegistered;
        windows.AddWindow(mainWindow);
        windows.AddWindow(debugWindow);
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMain;
        Framework.Update += OnUpdate;
        Log.Information("Tomes & Tides plugin loaded at {LoadedUtc:O}; build={BuildVersion}.",
            loadedUtc, typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown");
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        Commands.RemoveHandler(MainCommand);
        if (aliasRegistered)
            Commands.RemoveHandler(Alias);
        dungeonRun.Dispose();
        retainerHandoff.Dispose();
        oceanFishing.Dispose();
        autoRetainer.Dispose();
        gameState.Dispose();
        windows.RemoveAllWindows();
    }

    private void OnUpdate(IFramework framework)
    {
        retainerHandoff.RefreshResumeReadiness();
        oceanFishing.RefreshResumeReadiness();
        var oceanOwns = oceanFishing.OwnsActivity;
        var retainerOwns = retainerHandoff.OwnsActivity;
        if (oceanOwns && retainerOwns)
        {
            automationSession.RequestStop("Conflicting persisted provider ownership detected; recovery required.");
            oceanFishing.Tick(allowNewWindow: false);
            retainerHandoff.Tick(allowNewWork: false);
        }
        else
        {
            farmingPlan.Tick(oceanOwns || retainerOwns, allowDispatch: false);
            oceanFishing.Tick();
            var fishingPriority = oceanFishing.PrioritizesNow;
            retainerHandoff.Tick(allowNewWork: !fishingPriority && !oceanFishing.OwnsActivity);
            var anyOwner = oceanFishing.OwnsActivity || retainerHandoff.OwnsActivity;
            var parentActive = configuration.AutomationSession is { State: "Active", StopRequested: false };
            farmingPlan.Tick(anyOwner || !parentActive, allowDispatch: parentActive && !anyOwner && !fishingPriority);
        }
        dungeonRun.Tick();
        session.Tick();
        if (configuration.MonitorRetainerVentures && DateTimeOffset.UtcNow >= nextRetainerScanUtc)
        {
            RefreshRetainerReadiness();
            nextRetainerScanUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        }
        if (DateTimeOffset.UtcNow >= nextRosterScanUtc && configuration.OceanFishingCharacterMode == "SelectedCharacter")
        {
            nextRosterScanUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            retainerRosterKnown = autoRetainer.TryGetRegisteredCharacters(out retainerRoster);
        }
        automationSession.CompleteStopIfSettled(oceanFishing.OwnsActivity || retainerHandoff.OwnsActivity ||
            dungeonRun.Current is not null);
        if (DateTimeOffset.UtcNow < nextDashboardRefreshUtc)
            return;

        var mode = Enum.IsDefined(typeof(DebugTests.DutyModeSelection), configuration.MainSelectedDutyMode)
            ? (DebugTests.DutyModeSelection)configuration.MainSelectedDutyMode
            : DebugTests.DutyModeSelection.Support;
        var configurationChanged = false;
        if (configuration.MainSelectedDutyMode is < 0 or > 1)
        {
            configuration.MainSelectedDutyMode = (int)mode;
            configurationChanged = true;
        }
        session.RefreshDashboardSnapshot(configuration.MainSelectedTerritory, mode);
        if (session.DungeonChoices.Count > 0 &&
            !session.DungeonChoices.Any(choice => choice.Territory == configuration.MainSelectedTerritory))
        {
            configuration.MainSelectedTerritory = session.DungeonChoices[0].Territory;
            configurationChanged = true;
        }
        if (configurationChanged)
            SaveConfiguration();
        nextDashboardRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(5);
    }
    private void SaveConfiguration() => PluginInterface.SavePluginConfig(configuration);
    private RetainerReadinessSnapshot RefreshRetainerReadiness()
    {
        retainerReadiness = autoRetainer.ReadReadiness(PlayerState.ContentId);
        nextRetainerScanUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        return retainerReadiness;
    }
    private void OpenMain() => mainWindow.IsOpen = true;
    private void OpenDiagnostics() => debugWindow.IsOpen = true;
    private void OnCommand(string command, string arguments) => mainWindow.Toggle();
}
