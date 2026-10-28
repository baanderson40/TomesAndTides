using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TomesAndTides.Debugging;

namespace TomesAndTides.Windows;

/// <summary>Manual one-run test controls, diagnostic runner, and detailed result viewer.</summary>
public sealed class DebugWindow : Window
{
    private enum SelectedDiagnosticPanel { Test, ManualOneRun, AutomationState }

    private readonly DebugSession session;
    private readonly Configuration configuration;
    private readonly DungeonRunController dungeonRun;
    private readonly FarmingPlanController farmingPlan;
    private readonly RetainerHandoffController retainerHandoff;
    private readonly AutomationSessionController automationSession;
    private readonly OceanFishingHandoffController oceanFishing;
    private readonly Action saveConfiguration;
    private readonly IPluginLog log;
    private readonly Action openMain;
    private int selected;
    private SelectedDiagnosticPanel selectedPanel;
    private bool confirmRun;
    private DebugTests.DungeonOption? confirmedDungeon;
    private DebugTests.DutyModeSelection confirmedMode;
    private bool confirmedBareMode;
    private string? actionMessage;

    public bool AliasAvailable { get; set; }

    public DebugWindow(DebugSession session, Configuration configuration, DungeonRunController dungeonRun,
        FarmingPlanController farmingPlan, RetainerHandoffController retainerHandoff,
        AutomationSessionController automationSession, OceanFishingHandoffController oceanFishing,
        Action saveConfiguration, IPluginLog log, Action openMain)
        : base("Tomes & Tides — Diagnostics##TomesAndTidesDiagnostics", ImGuiWindowFlags.NoCollapse)
    {
        this.session = session;
        this.configuration = configuration;
        this.dungeonRun = dungeonRun;
        this.farmingPlan = farmingPlan;
        this.retainerHandoff = retainerHandoff;
        this.automationSession = automationSession;
        this.oceanFishing = oceanFishing;
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

    private void DrawAutomationStateDebug()
    {
        ImGui.TextWrapped("AUTOMATION STATE DEBUG");
        var plan = farmingPlan.Current;
        var run = dungeonRun.Current;
        var parent = automationSession.Current;
        var retainerSession = retainerHandoff.Session;
        var handoff = retainerHandoff.Handoff;
        var fishing = oceanFishing.Current;
        var currentChecklist = farmingPlan.CurrentChecklist;
        var sliderEnabled = farmingPlan.CanStart && run is null;
        var schedulerPeriod = plan is null
            ? "none"
            : FarmingSchedulePolicy.GetPeriodStartUtc(DateTimeOffset.UtcNow, plan.WeekStartUtc).ToString("O");
        var checklistSummary = currentChecklist is null
            ? "none"
            : $"{currentChecklist.StartUtc:O} {currentChecklist.CompletedRuns}/{currentChecklist.AllocatedRuns}";
        var activeRunCredited = run is not null && plan?.CreditedOperationIds.Contains(run.OperationId) == true;
        var schedulerStart = plan is null
            ? (DateTimeOffset?)null
            : FarmingSchedulePolicy.GetPeriodStartUtc(DateTimeOffset.UtcNow, plan.WeekStartUtc);
        var checklistPointerMismatch = schedulerStart is { } currentStart && plan?.CurrentPeriodStartUtc != currentStart;
        if (ImGui.Button("Output automation state to Dalamud log", new Vector2(-1, 0)))
        {
            var output = new StringBuilder()
                .AppendLine("Tomes & Tides automation state (cached; no provider calls)")
                .AppendLine($"Captured UTC: {DateTimeOffset.UtcNow:O}")
                .AppendLine($"Finish-period slider: {(sliderEnabled ? "enabled" : "disabled")}; FarmingPlan.CanStart={farmingPlan.CanStart}; dungeon run present={run is not null}")
                .AppendLine($"Saved finish periods: config={configuration.FarmingPeriodsToUse}; plan={plan?.RequestedPeriodCount.ToString() ?? "none"}")
                .AppendLine($"Plan finish deadline UTC: {plan?.FinishDeadlineUtc.ToString("O") ?? "none"}; weekly reset UTC: {plan?.WeeklyResetUtc.ToString("O") ?? "none"}")
                .AppendLine($"Scheduler UTC period: {schedulerPeriod}")
                .AppendLine($"Saved checklist pointer: {plan?.CurrentPeriodStartUtc?.ToString("O") ?? "none"}; displayed current checklist: {checklistSummary}")
                .AppendLine($"Checklist pointer differs from scheduler period={checklistPointerMismatch}")
                .AppendLine($"Active run period: {run?.FarmingPeriodStartUtc?.ToString("O") ?? "none"}; linked operation={run?.OperationId ?? "none"}; already credited={activeRunCredited}")
                .AppendLine($"Persisted period-record count: {plan?.Periods.Count ?? 0}")
                .AppendLine($"Start action: plan={plan?.State ?? "none"}; parent session={parent?.State ?? "none"}; dungeon run={run?.State ?? "none"}; retainer owns={retainerHandoff.OwnsActivity}; fishing owns={oceanFishing.OwnsActivity}")
                .AppendLine($"Parent session: state={parent?.State ?? "none"}; recovery={parent?.RecoveryKind ?? "none"}; stopRequested={parent?.StopRequested.ToString() ?? "n/a"}; detail={parent?.Detail ?? "none"}")
                .AppendLine($"Farming plan: state={plan?.State ?? "none"}; recovery={plan?.RecoveryKind ?? "none"}; stopRequested={plan?.StopRequested.ToString() ?? "n/a"}; detail={plan?.Detail ?? "none"}")
                .AppendLine($"Dungeon run: state={run?.State ?? "none"}; reward={run?.RewardOutcome ?? "n/a"}; detail={run?.Detail ?? "none"}")
                .AppendLine($"Retainer session: state={retainerSession?.State ?? "none"}; recovery={retainerSession?.RecoveryKind ?? "none"}; stopRequested={retainerSession?.StopRequested.ToString() ?? "n/a"}; detail={retainerSession?.Detail ?? "none"}")
                .AppendLine($"Retainer handoff: state={handoff?.State ?? "none"}; recovery={handoff?.RecoveryKind ?? "none"}; detail={handoff?.Detail ?? "none"}")
                .AppendLine($"Ocean Fishing: state={fishing?.State ?? "none"}; recovery={fishing?.RecoveryKind ?? "none"}; detail={fishing?.Detail ?? "none"}")
                .AppendLine($"Ocean Fishing admission: {oceanFishing.Admission?.Code ?? "none"}; {oceanFishing.Admission?.Detail ?? "no cached admission diagnostic"}")
                .AppendLine($"Resume readiness (cached): plan={farmingPlan.CanResumeAfterReload}; retainers={retainerHandoff.CanResumeAutomationSession}; fishing={oceanFishing.CanResumeAutomationSession}")
                .ToString();
            log.Information("Automation state debug output (cached; no provider calls):\n{AutomationState}", output);
            actionMessage = "Cached automation state written to the Dalamud log. Character IDs are not included.";
        }
        TextDisabledWrapped("Writes cached controller states, recovery reasons, and the finish-slider gate to the Dalamud log. No providers are queried and character IDs are omitted.");
        if (!string.IsNullOrWhiteSpace(actionMessage))
            TextColoredWrapped(TomesTheme.Amber, actionMessage);
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
        ImGui.TextWrapped("AutoDuty must be stopped before launch. TT sets the selected duty mode and requests one loop automatically; these settings persist after the run. TT rechecks eligibility before dispatch.");
        ImGui.TextWrapped("TT updates the selected duty mode and AutoDuty.Run selects Looping mode with one loop. TT never pushes or pops global config overrides. Keep other integrations from controlling AutoDuty or pushing overrides until the run is safely acknowledged.");
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
            if (ImGui.Selectable(session.Tests[index].Name,
                    selectedPanel == SelectedDiagnosticPanel.Test && selected == index))
            {
                selected = index;
                selectedPanel = SelectedDiagnosticPanel.Test;
            }
        if (ImGui.Selectable("Manual one-run test", selectedPanel == SelectedDiagnosticPanel.ManualOneRun))
            selectedPanel = SelectedDiagnosticPanel.ManualOneRun;
        if (ImGui.Selectable("Automation state", selectedPanel == SelectedDiagnosticPanel.AutomationState))
            selectedPanel = SelectedDiagnosticPanel.AutomationState;

        ImGui.Spacing();
        if (selectedPanel != SelectedDiagnosticPanel.Test)
        {
            ImGui.TextWrapped(selectedPanel == SelectedDiagnosticPanel.ManualOneRun
                ? "Explicitly request one AutoDuty dungeon run after fresh validation."
                : "Copy cached automation and recovery state for troubleshooting.");
            ImGui.EndChild();
            return;
        }
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
        ImGui.TextWrapped(selectedPanel switch
        {
            SelectedDiagnosticPanel.ManualOneRun => "Manual one-run test",
            SelectedDiagnosticPanel.AutomationState => "Automation state",
            _ => session.Tests[selected].Name,
        });
        ImGui.Separator();
        if (selectedPanel == SelectedDiagnosticPanel.ManualOneRun)
        {
            DrawOneRunTest();
            ImGui.EndChild();
            return;
        }
        if (selectedPanel == SelectedDiagnosticPanel.AutomationState)
        {
            DrawAutomationStateDebug();
            ImGui.EndChild();
            return;
        }
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
