using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TomesAndTides.Debugging;

namespace TomesAndTides.Windows;

/// <summary>User-facing Tomes &amp; Tides window. Live values arrive as framework snapshots.</summary>
public sealed class MainWindow : Window
{
    private readonly DebugSession session;
    private readonly Configuration configuration;
    private readonly DungeonRunController dungeonRun;
    private readonly Action saveConfiguration;
    private readonly Action openDiagnostics;
    private readonly IPluginLog log;
    private DebugTests.DungeonOption? confirmedDungeon;
    private DebugTests.DutyModeSelection confirmedMode;
    private bool confirmedBareMode;
    private bool confirmRun;
    private string? actionMessage;

    public bool AliasAvailable { get; set; }

    public MainWindow(DebugSession session, Configuration configuration, DungeonRunController dungeonRun,
        Action saveConfiguration, Action openDiagnostics, IPluginLog log)
        : base("Tomes & Tides##TomesAndTidesMain")
    {
        this.session = session;
        this.configuration = configuration;
        this.dungeonRun = dungeonRun;
        this.saveConfiguration = saveConfiguration;
        this.openDiagnostics = openDiagnostics;
        this.log = log;
        Size = new Vector2(900, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw() => TomesTheme.Push();
    public override void PostDraw() => TomesTheme.Pop();

    public override void Draw()
    {
        ImGui.TextColored(TomesTheme.Cyan, "TOMES & TIDES");
        ImGui.SameLine();
        ImGui.TextDisabled("Weekly tome farming · one-run test");
        if (!AliasAvailable)
            ImGui.TextWrapped("/tt is already in use. Open this window with /tomestides.");
        ImGui.Separator();

        DrawTomeSummary();
        ImGui.Spacing();
        DrawDungeonSetup();
        ImGui.Spacing();
        DrawRunPanel();
        ImGui.Spacing();
        if (ImGui.Button("Diagnostics", new Vector2(150 * TomesTheme.Scale, 0)))
            openDiagnostics();
        ImGui.SameLine();
        ImGui.TextDisabled("Read-only tests and detailed reports are kept in a separate window.");

        DrawConfirmation();
    }

    private void DrawTomeSummary()
    {
        ImGui.TextUnformatted("CURRENT CHARACTER");
        var snapshot = session.Dashboard;
        if (snapshot is null)
        {
            ImGui.TextWrapped("Waiting for the first framework snapshot…");
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            ImGui.TextColored(TomesTheme.Amber, $"Snapshot issue: {snapshot.Error}");
        if (!snapshot.LoggedIn)
        {
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(snapshot.Error)
                ? "Log in to see live character and tome balances."
                : "Live character and tome values are unavailable; refresh will retry.");
            ImGui.TextDisabled($"Snapshot: {snapshot.CapturedUtc.ToLocalTime():t}");
            return;
        }

        ImGui.TextUnformatted(snapshot.CharacterName);
        ImGui.SameLine();
        ImGui.TextDisabled($"Updated {snapshot.CapturedUtc.ToLocalTime():t}");
        if (snapshot.WeeklyAcquired is { } acquired && snapshot.WeeklyLimit is > 0 and var limit)
        {
            var progress = Math.Clamp((float)acquired / limit, 0, 1);
            ImGui.TextUnformatted($"Weekly {snapshot.CurrencyName}: {acquired:N0} / {limit:N0}");
            ImGui.ProgressBar(progress, new Vector2(-1, 18), $"{Math.Max(0, limit - acquired):N0} remaining");
        }
        else
            ImGui.TextColored(TomesTheme.Amber, $"Weekly {snapshot.CurrencyName}: unavailable");

        if (snapshot.HeldBalance is { } held)
            ImGui.TextUnformatted($"Held: {held:N0} / 2,000");
        else
            ImGui.TextColored(TomesTheme.Amber, "Held balance: unavailable");

        var providerText = snapshot.AutoDutyStopped switch
        {
            true => "AutoDuty idle",
            false => "AutoDuty active",
            null => "AutoDuty state unavailable",
        };
        ImGui.SameLine();
        ImGui.TextColored(snapshot.AutoDutyStopped == true ? TomesTheme.Mint : TomesTheme.Amber, providerText);
    }

    private void DrawDungeonSetup()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("DUNGEON SETUP");
        var choices = session.DungeonChoices;
        var selectedTerritory = configuration.MainSelectedTerritory;
        var current = choices.FirstOrDefault(dungeon => dungeon.Territory == selectedTerritory);
        if (current is null && choices.Count > 0)
            current = choices[0];

        if (choices.Count == 0)
        {
            ImGui.TextWrapped("Dungeon data is still loading. If unavailable, open Diagnostics and run Dungeon rewards.");
            return;
        }

        var label = current is null ? "Select dungeon" : $"{current.Name} — {current.Reward} tomes";
        if (ImGui.BeginCombo("Dungeon", label))
        {
            foreach (var choice in choices)
            {
                var choiceLabel = $"{choice.Name} — {choice.Reward} tomes##main-{choice.Territory}";
                if (ImGui.Selectable(choiceLabel, choice.Territory == current?.Territory))
                {
                    configuration.MainSelectedTerritory = choice.Territory;
                    SaveConfiguration("Main dungeon selection");
                }
            }
            ImGui.EndCombo();
        }

        var selectedMode = Math.Clamp(configuration.MainSelectedDutyMode, 0, 1);
        var modes = new[] { "Support", "Trust" };
        if (ImGui.Combo("NPC mode", ref selectedMode, modes, modes.Length))
        {
            configuration.MainSelectedDutyMode = selectedMode;
            SaveConfiguration("Main NPC mode selection");
        }
        ImGui.TextDisabled("Trust members and levels are selected and managed by AutoDuty.");

        var snapshot = session.Dashboard;
        var selectedModeValue = (DebugTests.DutyModeSelection)selectedMode;
        if (snapshot is null || snapshot.SelectedTerritory != current?.Territory ||
            snapshot.SelectedMode != selectedModeValue || snapshot.Eligibility is null)
        {
            ImGui.TextColored(TomesTheme.Amber, "Readiness: refreshing for this dungeon/mode…");
        }
        else
        {
            var readiness = snapshot.Eligibility;
            ImGui.TextColored(TomesTheme.ForStatus(readiness.Status), $"Launch readiness: {readiness.Status}");
            if (readiness.Status != "Eligible")
                ImGui.TextWrapped(SummarizeReadiness(readiness.Text));
        }
    }

    private void DrawRunPanel()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("AUTODUTY ONE-RUN TEST");
        var enableActions = configuration.EnableAutoDutyLoopActions;
        if (ImGui.Checkbox("Enable AutoDuty loop actions", ref enableActions))
        {
            var previous = configuration.EnableAutoDutyLoopActions;
            configuration.EnableAutoDutyLoopActions = enableActions;
            SaveConfiguration("Enable AutoDuty loop actions", previous);
        }
        ImGui.TextWrapped(enableActions
            ? "Enabled: AutoDuty's configured pre-loop, between-loop, and termination actions may execute."
            : "Off by default: request bareMode=true to suppress normal AutoDuty loop-action groups. Locked/internal actions may still be evaluated.");

        if (dungeonRun.Current is { } run)
        {
            ImGui.TextColored(TomesTheme.ForStatus(run.State), $"{run.State} · {run.DungeonName}");
            ImGui.TextDisabled($"Reward: {run.RewardOutcome} · expected {run.ExpectedReward}" +
                               (run.ObservedWeeklyDelta is { } delta ? $" · observed +{delta}" : string.Empty));
            ImGui.TextWrapped(run.Detail);
            if (run.WeeklyAcquiredAfter is { } after)
                ImGui.TextUnformatted($"Weekly acquired: {run.WeeklyAcquiredBefore:N0} → {after:N0}");
            if (dungeonRun.CanAcknowledge && ImGui.Button("Acknowledge and release TT ownership"))
                dungeonRun.Acknowledge();
            return;
        }

        var territory = configuration.MainSelectedTerritory;
        var dungeon = session.DungeonChoices.FirstOrDefault(choice => choice.Territory == territory) ??
                      session.DungeonChoices.FirstOrDefault();
        var modeIndex = Math.Clamp(configuration.MainSelectedDutyMode, 0, 1);
        var snapshot = session.Dashboard;
        var readinessMatches = snapshot?.Eligibility?.Status == "Eligible" &&
                               snapshot.SelectedTerritory == dungeon?.Territory &&
                               snapshot.SelectedMode == (DebugTests.DutyModeSelection)modeIndex;
        ImGui.BeginDisabled(dungeon is null || !readinessMatches);
        if (ImGui.Button("Run one dungeon…", new Vector2(-1, 0)))
        {
            confirmedDungeon = dungeon;
            confirmedMode = (DebugTests.DutyModeSelection)modeIndex;
            confirmedBareMode = SingleRunPolicy.BareModeFor(configuration.EnableAutoDutyLoopActions);
            confirmRun = true;
            ImGui.OpenPopup("Confirm one AutoDuty run");
        }
        ImGui.EndDisabled();
        if (!readinessMatches)
            ImGui.TextDisabled("Start unlocks only when the latest framework snapshot reports Eligible for these selections.");
        if (!string.IsNullOrWhiteSpace(actionMessage))
            ImGui.TextWrapped(actionMessage);
    }

    private void DrawConfirmation()
    {
        if (!confirmRun || !ImGui.BeginPopupModal("Confirm one AutoDuty run", ref confirmRun,
                ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var dungeon = confirmedDungeon!;
        ImGui.TextWrapped($"Dungeon: {dungeon.Name}\nMode: {confirmedMode}\nExpected full-clear reward: {dungeon.Reward}\nbareMode: {confirmedBareMode}");
        ImGui.TextWrapped("AutoDuty must already be stopped, Looping, configured for one loop, and set to this mode. TT will recheck eligibility immediately before its single request.");
        ImGui.TextWrapped("TT takes responsibility for this one operation. AutoDuty has no cross-plugin ownership lock: do not run AutoDuty or push overrides from another integration until the run is safely acknowledged. TT will not write settings or call Push/PopConfigOverrides.");
        if (!confirmedBareMode)
            ImGui.TextColored(TomesTheme.Amber, "Warning: configured AutoDuty termination actions may log out or close the client.");
        ImGui.Separator();
        if (ImGui.Button("Confirm exactly one run"))
        {
            try
            {
                dungeonRun.Queue(new DungeonRunRequest(dungeon, confirmedMode, -1, confirmedBareMode));
                actionMessage = "Confirmed. Final checks are queued for the next framework update.";
            }
            catch (Exception error)
            {
                actionMessage = error.Message;
            }
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

    private void SaveConfiguration(string context, bool previousValue)
    {
        try
        {
            saveConfiguration();
            log.Information("Configuration changed: Enable AutoDuty loop actions={Enabled}; future bareMode={BareMode}.",
                configuration.EnableAutoDutyLoopActions,
                SingleRunPolicy.BareModeFor(configuration.EnableAutoDutyLoopActions));
            actionMessage = null;
        }
        catch (Exception error)
        {
            configuration.EnableAutoDutyLoopActions = previousValue;
            log.Error(error, "Could not save {Context}.", context);
            actionMessage = $"Could not save {context}: {error.Message}";
        }
    }

    private void SaveConfiguration(string context)
    {
        try
        {
            saveConfiguration();
            actionMessage = null;
        }
        catch (Exception error)
        {
            log.Error(error, "Could not save {Context}.", context);
            actionMessage = $"Could not save {context}: {error.Message}";
        }
    }

    private static string SummarizeReadiness(string text)
    {
        var lines = text.Split('\n');
        var blockers = lines.Where(line => line.Contains(": Blocked", StringComparison.OrdinalIgnoreCase) ||
                                           line.Contains(": Unknown", StringComparison.OrdinalIgnoreCase))
            .Take(3).ToArray();
        return blockers.Length > 0 ? string.Join("\n", blockers) : "See Diagnostics for the full readiness report.";
    }
}
