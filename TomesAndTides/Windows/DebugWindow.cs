using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TomesAndTides.Debugging;

namespace TomesAndTides.Windows;

/// <summary>Separate read-only test runner and detailed result viewer.</summary>
public sealed class DebugWindow : Window
{
    private readonly DebugSession session;
    private readonly Action openMain;
    private int selected;

    public bool AliasAvailable { get; set; }

    public DebugWindow(DebugSession session, Action openMain)
        : base("Tomes & Tides — Diagnostics##TomesAndTidesDiagnostics", ImGuiWindowFlags.NoCollapse)
    {
        this.session = session;
        this.openMain = openMain;
        Size = new Vector2(1000, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw() => TomesTheme.Push();
    public override void PostDraw() => TomesTheme.Pop();

    public override void Draw()
    {
        ImGui.TextColored(TomesTheme.Cyan, "DIAGNOSTICS");
        ImGui.SameLine();
        if (ImGui.SmallButton("Main window"))
            openMain();
        ImGui.SameLine();
        ImGui.TextDisabled("Read-only tests and retained snapshots");
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
    }

    private void DrawTests(float height)
    {
        if (!ImGui.BeginChild("DiagnosticsTests", new Vector2(0, height), true))
        {
            ImGui.EndChild();
            return;
        }

        ImGui.TextUnformatted("Debug tests");
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
        if (ImGui.Combo("NPC mode", ref mode, modes, modes.Length))
            session.SelectEligibility(session.SelectedTerritory, (DebugTests.DutyModeSelection)mode);

        var choices = session.DungeonChoices;
        if (choices.Count == 0)
        {
            ImGui.TextWrapped("Run Dungeon rewards to populate diagnostic candidates.");
            return;
        }

        var current = choices.FirstOrDefault(choice => choice.Territory == session.SelectedTerritory);
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
        ImGui.TextUnformatted(session.Tests[selected].Name);
        ImGui.Separator();
        if (session.GetResult(selected) is { } result)
        {
            var stale = result.SelectionFingerprint is not null &&
                        result.SelectionFingerprint != session.CurrentEligibilityFingerprint;
            ImGui.TextColored(TomesTheme.ForStatus(result.Report.Status),
                $"{result.Report.Status}{(stale ? " — STALE SELECTION" : string.Empty)} | Captured {result.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
            if (ImGui.Button("Copy result"))
                ImGui.SetClipboardText($"{session.Tests[selected].Name}\n{result.CapturedUtc:O}\n{result.Report.Status}\n\n{result.Report.Text}");
            ImGui.Separator();
            ImGui.TextUnformatted(result.Report.Text);
        }
        else
            ImGui.TextWrapped("No result yet. Choose a test and click Run selected, or Run all.");
        ImGui.EndChild();
    }
}
