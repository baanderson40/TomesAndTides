using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using TomesAndTides.Debugging;
using TomesAndTides.Windows;

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
    private readonly bool aliasRegistered;
    private DateTimeOffset nextDashboardRefreshUtc;

    public Plugin()
    {
        var loadedUtc = DateTimeOffset.UtcNow;
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var debugTests = new DebugTests(Data, Client, Objects, PluginInterface, Unlocks, PlayerState, Conditions, configuration);
        session = new DebugSession(debugTests, Log);
        dungeonRun = new DungeonRunController(configuration, SaveConfiguration, PluginInterface,
            Client, Objects, PlayerState, DutyState, Conditions, Log, debugTests.CheckEligibility,
            debugTests.ReadLiveTomeState);
        farmingPlan = new FarmingPlanController(configuration, SaveConfiguration, dungeonRun, PlayerState, Log,
            debugTests.ReadLiveTomeState, debugTests.CheckEligibility,
            territory => session.DungeonChoices.FirstOrDefault(choice => choice.Territory == territory));
        farmingPlan.RecoverAfterReload();
        mainWindow = new MainWindow(session, configuration, dungeonRun, farmingPlan, SaveConfiguration, OpenDiagnostics, Log);
        debugWindow = new DebugWindow(session, OpenMain);
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
        windows.RemoveAllWindows();
    }

    private void OnUpdate(IFramework framework)
    {
        farmingPlan.Tick();
        dungeonRun.Tick();
        session.Tick();
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
    private void OpenMain() => mainWindow.IsOpen = true;
    private void OpenDiagnostics() => debugWindow.IsOpen = true;
    private void OnCommand(string command, string arguments) => mainWindow.Toggle();
}
