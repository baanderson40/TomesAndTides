using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TomesAndTides.Debugging;

namespace TomesAndTides.Windows;

/// <summary>Manual one-run test controls, diagnostic runner, and detailed result viewer.</summary>
public sealed class DebugWindow : Window
{
    private readonly DebugSession session;
    private readonly Configuration configuration;
    private readonly DungeonRunController dungeonRun;
    private readonly FarmingPlanController farmingPlan;
    private readonly RetainerHandoffController retainerHandoff;
    private readonly Action saveConfiguration;
    private readonly IPluginLog log;
    private readonly Action openMain;
    private int selected;
    private bool confirmRun;
    private DebugTests.DungeonOption? confirmedDungeon;
    private DebugTests.DutyModeSelection confirmedMode;
    private bool confirmedBareMode;
    private string? actionMessage;

    public bool AliasAvailable { get; set; }

    public DebugWindow(DebugSession session, Configuration configuration, DungeonRunController dungeonRun,
        FarmingPlanController farmingPlan, RetainerHandoffController retainerHandoff,
        Action saveConfiguration, IPluginLog log, Action openMain)
        : base("Tomes & Tides — Diagnostics##TomesAndTidesDiagnostics", ImGuiWindowFlags.NoCollapse)
    {
        this.session = session;
        this.configuration = configuration;
        this.dungeonRun = dungeonRun;
        this.farmingPlan = farmingPlan;
        this.retainerHandoff = retainerHandoff;
        this.saveConfiguration = saveConfiguration;
        this.log = log;
        this.openMain = openMain;
        Size = new Vector2(1000, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private static void TextDisabledWrapped(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    private static void TextColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    public override void PreDraw() => TomesTheme.Push();
    public override void PostDraw() => TomesTheme.Pop();

    public override void Draw()
    {
        TextColoredWrapped(TomesTheme.Cyan, "DIAGNOSTICS");
        ImGui.SameLine();
        if (ImGui.SmallButton("Main window"))
            openMain();
        ImGui.SameLine();
        TextDisabledWrapped("Explicit one-run test · diagnostic snapshots");
        if (!AliasAvailable)
            ImGui.TextWrapped("/tt is already in use. Open the main window with /tomestides.");
        ImGui.Separator();

        DrawOneRunTest();
        ImGui.Spacing();

        var height = Math.Max(1, ImGui.GetContentRegionAvail().Y - 4);
        if (!ImGui.BeginTable("DiagnosticsLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            return;
        ImGui.TableSetupColumn("Tests", ImGuiTableColumnFlags.WidthFixed, 250 * TomesTheme.Scale);
        ImGui.TableSetupColumn("Results", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawTests(height);
        ImGui.TableNextColumn();
        DrawResults(height);
        ImGui.EndTable();
        DrawRunConfirmation();
    }

    private void DrawOneRunTest()
    {
        ImGui.TextWrapped("MANUAL ONE-RUN TEST");
        var enableActions = configuration.EnableAutoDutyLoopActions;
        if (ImGui.Checkbox("Enable AutoDuty loop actions", ref enableActions))
        {
            var previous = configuration.EnableAutoDutyLoopActions;
            configuration.EnableAutoDutyLoopActions = enableActions;
            try
            {
                saveConfiguration();
                log.Information("Configuration changed: Enable AutoDuty loop actions={Enabled}; future bareMode={BareMode}.",
                    enableActions, SingleRunPolicy.BareModeFor(enableActions));
            }
            catch (Exception error)
            {
                configuration.EnableAutoDutyLoopActions = previous;
                actionMessage = $"Could not save AutoDuty loop-action setting: {error.Message}";
            }
        }
        TextDisabledWrapped(enableActions
            ? "Configured pre-loop, between-loop, and termination actions may execute."
            : "Off by default: bareMode=true suppresses normal AutoDuty loop-action groups; locked/internal actions may still be evaluated.");

        var dungeon = session.DungeonChoices.FirstOrDefault(choice => choice.Territory == configuration.MainSelectedTerritory) ??
                      session.DungeonChoices.FirstOrDefault();
        var mode = (DebugTests.DutyModeSelection)Math.Clamp(configuration.MainSelectedDutyMode, 0, 1);
        var snapshot = session.Dashboard;
        var readinessMatches = snapshot?.Eligibility?.Status == "Eligible" &&
                               snapshot.SelectedTerritory == dungeon?.Territory && snapshot.SelectedMode == mode;
        var retainerSessionActive = retainerHandoff.Session is { State: "Active" or "StopRequested" };
        ImGui.BeginDisabled(dungeon is null || !readinessMatches || farmingPlan.BlocksManualRun ||
                            dungeonRun.Current is not null || retainerSessionActive || retainerHandoff.OwnsActivity);
        if (ImGui.Button("Run one dungeon…"))
        {
            confirmedDungeon = dungeon;
            confirmedMode = mode;
            confirmedBareMode = SingleRunPolicy.BareModeFor(configuration.EnableAutoDutyLoopActions);
            confirmRun = true;
            ImGui.OpenPopup("Confirm one AutoDuty run");
        }
        ImGui.EndDisabled();
        if (!readinessMatches)
            TextDisabledWrapped("Available only when the current main-window dungeon and mode have a fresh Eligible snapshot.");
        if (retainerSessionActive || retainerHandoff.OwnsActivity)
            TextDisabledWrapped("Manual runs are blocked while the retainer automation session owns activity.");
        if (!string.IsNullOrWhiteSpace(actionMessage))
            ImGui.TextWrapped(actionMessage);
        ImGui.Separator();
    }

    private void DrawRunConfirmation()
    {
        if (!confirmRun || !ImGui.BeginPopupModal("Confirm one AutoDuty run", ref confirmRun,
                ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var dungeon = confirmedDungeon!;
        ImGui.TextWrapped($"Dungeon: {dungeon.Name}\nMode: {confirmedMode}\nExpected full-clear reward: {dungeon.Reward}\nbareMode: {confirmedBareMode}");
        ImGui.TextWrapped("AutoDuty must already be stopped, Looping, configured for one loop, and set to this mode. TT rechecks eligibility before its single request.");
        ImGui.TextWrapped("Keep other integrations from controlling AutoDuty or pushing overrides until the run is safely acknowledged. TT will not write AutoDuty settings or push/pop overrides.");
        if (!confirmedBareMode)
            TextColoredWrapped(TomesTheme.Amber, "Warning: configured AutoDuty termination actions may log out or close the client.");
        ImGui.Separator();
        if (ImGui.Button("Confirm exactly one run"))
        {
            try
            {
                dungeonRun.Queue(new DungeonRunRequest(dungeon, confirmedMode, -1, confirmedBareMode));
                actionMessage = "Confirmed. Final checks are queued for the next framework update.";
            }
            catch (Exception error) { actionMessage = error.Message; }
            confirmRun = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            confirmRun = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawTests(float height)
    {
        if (!ImGui.BeginChild("DiagnosticsTests", new Vector2(0, height), true))
        {
            ImGui.EndChild();
            return;
        }

        ImGui.TextWrapped("Debug tests");
        ImGui.Separator();
        for (var index = 0; index < session.Tests.Count; index++)
            if (ImGui.Selectable(session.Tests[index].Name, selected == index))
                selected = index;

        ImGui.Spacing();
        ImGui.TextWrapped(session.Tests[selected].Description);
        if (session.Tests[selected].Name is "Dungeon eligibility" or "AutoDuty launch readiness")
            DrawEligibilitySelection();
        ImGui.Spacing();
        if (ImGui.Button("Run selected", new Vector2(-1, 0)))
            session.Queue(selected);
        if (ImGui.Button("Run all", new Vector2(-1, 0)))
            session.QueueAll();
        if (ImGui.Button("Clear results", new Vector2(-1, 0)))
            session.Clear();
        ImGui.Spacing();
        ImGui.TextWrapped(session.HasPending ? "Tests queued for framework update…" : "Tests run only when requested.");
        ImGui.EndChild();
    }

    private void DrawEligibilitySelection()
    {
        var mode = (int)session.SelectedMode;
        var modes = new[] { "Support", "Trust" };
        ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.Combo("NPC mode", ref mode, modes, modes.Length))
            session.SelectEligibility(session.SelectedTerritory, (DebugTests.DutyModeSelection)mode);

        var choices = session.DungeonChoices;
        if (choices.Count == 0)
        {
            ImGui.TextWrapped("Run Dungeon rewards to populate diagnostic candidates.");
            return;
        }

        var current = choices.FirstOrDefault(choice => choice.Territory == session.SelectedTerritory);
        ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Dungeon", current is null ? "Select dungeon" : $"{current.Name} ({current.Reward})"))
        {
            foreach (var choice in choices)
            {
                var modeSupported = session.SelectedMode == DebugTests.DutyModeSelection.Support ? choice.Support : choice.Trust;
                var label = $"{choice.Name} — {choice.Reward} tomes" +
                            (modeSupported ? string.Empty : " (mode unsupported)") + $"##{choice.Territory}";
                if (ImGui.Selectable(label, choice.Territory == session.SelectedTerritory))
                    session.SelectEligibility(choice.Territory, session.SelectedMode);
            }
            ImGui.EndCombo();
        }

        var gearsets = session.GearsetChoices;
        var gearset = gearsets.FirstOrDefault(option => option.Id == session.SelectedGearsetId);
        var gearsetLabel = session.SelectedGearsetId < 0
            ? "Current equipment"
            : gearset is null ? "Selected set unavailable" : $"{gearset.Name} — {gearset.JobName}, iLvl {gearset.ItemLevel}";
        ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("Gearset inspection", gearsetLabel))
        {
            if (ImGui.Selectable("Current equipment", session.SelectedGearsetId < 0))
                session.SelectGearset(-1);
            foreach (var option in gearsets)
            {
                var optionLabel = $"{option.Name} — {option.JobName}, iLvl {option.ItemLevel}" +
                                  (option.CombatJob ? string.Empty : " (not combat)") + $"##{option.Id}";
                if (ImGui.Selectable(optionLabel, option.Id == session.SelectedGearsetId))
                    session.SelectGearset(option.Id);
            }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("Stored gearsets are inspected only; diagnostics never equip one. Launch selections are separate in the main window.");
    }

    private void DrawResults(float height)
    {
        if (!ImGui.BeginChild("DiagnosticsResults", new Vector2(0, height), true, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }
        ImGui.TextWrapped(session.Tests[selected].Name);
        ImGui.Separator();
        if (session.GetResult(selected) is { } result)
        {
            var stale = result.SelectionFingerprint is not null &&
                        result.SelectionFingerprint != session.CurrentEligibilityFingerprint;
            TextColoredWrapped(TomesTheme.ForStatus(result.Report.Status),
                $"{result.Report.Status}{(stale ? " — STALE SELECTION" : string.Empty)} | Captured {result.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
            if (ImGui.Button("Copy result"))
                ImGui.SetClipboardText($"{session.Tests[selected].Name}\n{result.CapturedUtc:O}\n{result.Report.Status}\n\n{result.Report.Text}");
            ImGui.Separator();
            ImGui.TextWrapped(result.Report.Text);
        }
        else
            ImGui.TextWrapped("No result yet. Choose a test and click Run selected, or Run all.");
        ImGui.EndChild();
    }
}
