using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TomesAndTides.Debugging;
using TomesAndTides.Integrations;

namespace TomesAndTides.Windows;

/// <summary>User-facing Tomes &amp; Tides window. Live values arrive as framework snapshots.</summary>
public sealed class MainWindow : Window
{
    private readonly DebugSession session;
    private readonly Configuration configuration;
    private readonly DungeonRunController dungeonRun;
    private readonly FarmingPlanController farmingPlan;
    private readonly Action saveConfiguration;
    private readonly Action openDiagnostics;
    private readonly IPluginLog log;
    private readonly Func<RetainerReadinessSnapshot?> readRetainerReadiness;
    private readonly RetainerHandoffController retainerHandoff;
    private bool confirmFarming;
    private DebugTests.DungeonOption? confirmedFarmingDungeon;
    private DebugTests.DutyModeSelection confirmedFarmingMode;
    private bool confirmedFarmingBareMode;
    private int confirmedFarmingPeriods;
    private bool confirmedServiceRetainers;
    private int confirmedRetainerWaitMinutes;
    private bool confirmedIsResume;
    private string? actionMessage;

    public bool AliasAvailable { get; set; }

    public MainWindow(DebugSession session, Configuration configuration, DungeonRunController dungeonRun,
        FarmingPlanController farmingPlan,
        Action saveConfiguration, Action openDiagnostics, IPluginLog log,
        Func<RetainerReadinessSnapshot?> readRetainerReadiness, RetainerHandoffController retainerHandoff)
        : base("Tomes & Tides##TomesAndTidesMain")
    {
        this.session = session;
        this.configuration = configuration;
        this.dungeonRun = dungeonRun;
        this.farmingPlan = farmingPlan;
        this.saveConfiguration = saveConfiguration;
        this.openDiagnostics = openDiagnostics;
        this.log = log;
        this.readRetainerReadiness = readRetainerReadiness;
        this.retainerHandoff = retainerHandoff;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Bug,
            IconOffset = new Vector2(1, 1),
            Priority = int.MinValue,
            ShowTooltip = () => ImGui.SetTooltip("Diagnostics"),
            Click = _ => openDiagnostics(),
        });
        Size = new Vector2(900, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 480),
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
        TextColoredWrapped(TomesTheme.Cyan, "TOMES & TIDES");
        ImGui.SameLine();
        TextDisabledWrapped("Weekly tome farming");
        if (!AliasAvailable)
            ImGui.TextWrapped("/tt is already in use. Open this window with /tomestides.");
        ImGui.Separator();
        DrawRecoveryBanner();

        if (ImGui.BeginTabBar("TomesAndTidesMainTabs"))
        {
            if (ImGui.BeginTabItem("Current Character"))
            {
                DrawTomeSummary();
                ImGui.Spacing();
                DrawStartFarmButton();
                if (!string.IsNullOrWhiteSpace(actionMessage))
                    ImGui.TextWrapped(actionMessage);
                DrawFarmingConfirmation();
                ImGui.Spacing();
                DrawRunStatusPanel();
                DrawRetainerSessionSummary();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Farming Plan"))
            {
                DrawFarmingPanel();
                ImGui.Spacing();
                DrawFinishPeriodSelection();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Dungeon Setup"))
            {
                DrawDungeonSetup();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Retainers"))
            {
                DrawRetainerPanel();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawRecoveryBanner()
    {
        var run = dungeonRun.Current;
        var plan = farmingPlan.Current;
        if (run?.State != "RecoveryRequired" && plan?.State != "RecoveryRequired" && !retainerHandoff.NeedsRecovery)
            return;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, TomesTheme.Raised);
        if (ImGui.BeginChild("RecoveryBanner", new Vector2(0, 58 * TomesTheme.Scale), true))
        {
            TextColoredWrapped(TomesTheme.Amber, "RECOVERY REQUIRED — automation is paused");
            var detail = run?.State == "RecoveryRequired" ? run.Detail :
                plan?.State == "RecoveryRequired" ? plan.Detail :
                retainerHandoff.Session?.Detail ?? retainerHandoff.Handoff?.Detail ?? "Inspect the saved operation before resuming.";
            ImGui.TextWrapped(detail);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private void DrawTomeSummary()
    {
        ImGui.TextWrapped("CURRENT CHARACTER");
        var snapshot = session.Dashboard;
        if (snapshot is null)
        {
            ImGui.TextWrapped("Waiting for the first framework snapshot…");
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            TextColoredWrapped(TomesTheme.Amber, $"Snapshot issue: {snapshot.Error}");
        if (!snapshot.LoggedIn)
        {
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(snapshot.Error)
                ? "Log in to see live character and tome balances."
                : "Live character and tome values are unavailable; refresh will retry.");
            TextDisabledWrapped($"Snapshot: {snapshot.CapturedUtc.ToLocalTime():t}");
            return;
        }

        ImGui.TextWrapped(snapshot.CharacterName);
        ImGui.SameLine();
        TextDisabledWrapped($"Updated {snapshot.CapturedUtc.ToLocalTime():t}");
        if (snapshot.WeeklyAcquired is { } acquired && snapshot.WeeklyLimit is > 0 and var limit)
        {
            var progress = Math.Clamp((float)acquired / limit, 0, 1);
            ImGui.TextWrapped($"Weekly {snapshot.CurrencyName}: {acquired:N0} / {limit:N0}");
            ImGui.ProgressBar(progress, new Vector2(-1, 18), $"{Math.Max(0, limit - acquired):N0} remaining");
        }
        else
            TextColoredWrapped(TomesTheme.Amber, $"Weekly {snapshot.CurrencyName}: unavailable");

        if (snapshot.HeldBalance is { } held)
            ImGui.TextWrapped($"Held: {held:N0} / 2,000");
        else
            TextColoredWrapped(TomesTheme.Amber, "Held balance: unavailable");

        var providerText = snapshot.AutoDutyStopped switch
        {
            true => "AutoDuty idle",
            false => "AutoDuty active",
            null => "AutoDuty state unavailable",
        };
        ImGui.SameLine();
        TextColoredWrapped(snapshot.AutoDutyStopped == true ? TomesTheme.Mint : TomesTheme.Amber, providerText);
    }

    private void DrawDungeonSetup()
    {
        ImGui.Separator();
        ImGui.TextWrapped("DUNGEON SETUP");
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
        ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
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
        ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.Combo("NPC mode", ref selectedMode, modes, modes.Length))
        {
            configuration.MainSelectedDutyMode = selectedMode;
            SaveConfiguration("Main NPC mode selection");
        }
        TextDisabledWrapped("Trust members and levels are selected and managed by AutoDuty.");

        var snapshot = session.Dashboard;
        var selectedModeValue = (DebugTests.DutyModeSelection)selectedMode;
        if (snapshot is null || snapshot.SelectedTerritory != current?.Territory ||
            snapshot.SelectedMode != selectedModeValue || snapshot.Eligibility is null)
        {
            TextColoredWrapped(TomesTheme.Amber, "Readiness: refreshing for this dungeon/mode…");
        }
        else
        {
            var readiness = snapshot.Eligibility;
            TextColoredWrapped(TomesTheme.ForStatus(readiness.Status), $"Launch readiness: {readiness.Status}");
            if (readiness.Status != "Eligible")
                ImGui.TextWrapped(SummarizeReadiness(readiness.Text));
        }
    }

    private void DrawRunStatusPanel()
    {
        if (dungeonRun.Current is { } run)
        {
            ImGui.Separator();
            ImGui.TextWrapped("ACTIVE / RECOVERY RUN");
            TextColoredWrapped(TomesTheme.ForStatus(run.State), $"{run.State} · {run.DungeonName}");
            TextDisabledWrapped($"Reward: {run.RewardOutcome} · expected {run.ExpectedReward}" +
                               (run.ObservedWeeklyDelta is { } delta ? $" · observed +{delta}" : string.Empty));
            ImGui.TextWrapped(run.Detail);
            if (run.WeeklyAcquiredAfter is { } after)
                ImGui.TextWrapped($"Weekly acquired: {run.WeeklyAcquiredBefore:N0} → {after:N0}");
            if (run.FarmingPlanId is null && dungeonRun.CanAcknowledge && ImGui.Button("Acknowledge and release TT ownership"))
                dungeonRun.Acknowledge();
            return;
        }
    }

    private void DrawRetainerSessionSummary()
    {
        if (retainerHandoff.Session is not { } automationSession)
            return;
        ImGui.Separator();
        ImGui.TextWrapped("COMBINED AUTOMATION SESSION");
        TextColoredWrapped(TomesTheme.ForStatus(automationSession.State), automationSession.State);
        if (retainerHandoff.Handoff is { } handoff && retainerHandoff.OwnsActivity)
            ImGui.TextWrapped($"Current handoff: {handoff.State} · {handoff.Detail}");
        else
            ImGui.TextWrapped(automationSession.Detail);
        if (automationSession.State == "Active" && farmingPlan.Current is not { State: "Active" or "PendingValidation" } &&
            ImGui.Button("Stop automation", new Vector2(-1, 0)))
            retainerHandoff.RequestStop();
    }

    private void DrawFarmingPanel()
    {
        ImGui.Separator();
        ImGui.TextWrapped("WEEKLY TOME FARMING PLAN");
        var plan = farmingPlan.Current;
        if (plan is not null)
        {
            TextColoredWrapped(TomesTheme.ForStatus(plan.State), $"{plan.State} · {plan.DungeonName} · {plan.Mode}");
            ImGui.TextWrapped(plan.Detail);
            if (plan.FinishDeadlineUtc != default)
                ImGui.TextWrapped($"Finish deadline: {plan.FinishDeadlineUtc.ToLocalTime():f} · weekly reset {plan.WeeklyResetUtc.ToLocalTime():f}");
            if (plan.WeeklyLimitAtStart > 0 && session.Dashboard?.WeeklyAcquired is { } currentWeekly)
            {
                var remainingTomes = Math.Max(0, plan.WeeklyLimitAtStart - currentWeekly);
                ImGui.TextWrapped($"Remaining: {remainingTomes:N0} tomes · about {FarmingSchedulePolicy.RunsNeeded(currentWeekly, plan.WeeklyLimitAtStart, plan.DungeonReward)} runs");
            }
            if (plan.Blocker is { Length: > 0 } blocker)
                TextColoredWrapped(TomesTheme.Amber, $"Blocker: {blocker}");
            var currentPeriod = plan.CurrentPeriodStartUtc is { } start
                ? plan.Periods.FirstOrDefault(period => period.StartUtc == start)
                : null;
            if (currentPeriod is not null)
                ImGui.TextWrapped($"This period: {currentPeriod.CompletedRuns}/{currentPeriod.AllocatedRuns} runs · ends {currentPeriod.StartUtc.AddDays(1).ToLocalTime():f}");
            if (plan.RetainerCheckpointOperationId is { } checkpointOperation)
                TextColoredWrapped(TomesTheme.Cyan, $"Retainer checkpoint pending after verified run {checkpointOperation[..Math.Min(8, checkpointOperation.Length)]}…");
            foreach (var period in plan.Periods)
                TextDisabledWrapped($"{period.StartUtc.ToLocalTime():ddd HH:mm}: {period.CompletedRuns}/{period.AllocatedRuns} runs");
            ImGui.TextWrapped($"Verified runs: {plan.CreditedOperationIds.Count}");

            if (plan.State is "Active" or "PendingValidation")
            {
                if (ImGui.Button(plan.StopRequested ? "Stop requested" : "Stop automation", new Vector2(-1, 0)) && !plan.StopRequested)
                    retainerHandoff.RequestStop();
            }
            else if (farmingPlan.CanResumeAfterReload)
            {
                if (ImGui.Button("Acknowledge reload and resume plan", new Vector2(-1, 0)))
                    farmingPlan.ResumeAfterReload();
                if (ImGui.Button("Acknowledge recovery and stop plan", new Vector2(-1, 0)))
                    farmingPlan.AcknowledgeRecoveryAndStop();
            }
            else if (farmingPlan.CanAcknowledgeRecovery && ImGui.Button("Acknowledge recovery and stop plan", new Vector2(-1, 0)))
                farmingPlan.AcknowledgeRecoveryAndStop();
            if (plan.State is "Active" or "PendingValidation" or "RecoveryRequired")
                return;
        }
        else
            ImGui.TextWrapped("No weekly farming plan exists. Configure the dungeon and finish schedule, then start farming from Current Character.");
    }

    private void DrawFinishPeriodSelection()
    {
        ImGui.Separator();
        ImGui.TextWrapped("FINISH SCHEDULE");
        var finishPeriods = Math.Clamp(configuration.FarmingPeriodsToUse, 1, 7);
        ImGui.BeginDisabled(!farmingPlan.CanStart || dungeonRun.Current is not null);
        ImGui.TextWrapped("Finish within first weekly periods");
        ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.SliderInt("##FinishPeriods", ref finishPeriods, 1, 7))
        {
            var previous = configuration.FarmingPeriodsToUse;
            configuration.FarmingPeriodsToUse = finishPeriods;
            try { saveConfiguration(); }
            catch (Exception error)
            {
                configuration.FarmingPeriodsToUse = previous;
                actionMessage = $"Could not save period selection: {error.Message}";
            }
        }
        var finishBoundary = FarmingSchedulePolicy.GetWeekStartUtc(DateTimeOffset.UtcNow).AddDays(finishPeriods);
        TextDisabledWrapped($"Period boundary: 08:00 UTC · finish by {finishBoundary.ToLocalTime():f} local time");
        ImGui.TextWrapped("The selected number of reset-aligned daily periods defines the finish deadline. Starting mid-period uses only the remaining periods.");
        ImGui.EndDisabled();
        if (DateTimeOffset.UtcNow >= finishBoundary)
            TextColoredWrapped(TomesTheme.Amber, "Selected finish boundary has passed for this week.");
    }

    private void DrawStartFarmButton()
    {
        ImGui.Separator();
        ImGui.TextWrapped("FARMING PLAN");
        var plan = farmingPlan.Current;
        if (plan is { State: "Active" or "PendingValidation" or "RecoveryRequired" })
        {
            TextColoredWrapped(TomesTheme.ForStatus(plan.State), $"{plan.State} · {plan.DungeonName}");
            if (plan.State is "Active" or "PendingValidation")
            {
                if (ImGui.Button(plan.StopRequested ? "Stop requested" : "Stop automation", new Vector2(-1, 0)) &&
                    !plan.StopRequested)
                    retainerHandoff.RequestStop();
            }
            else
                TextDisabledWrapped("Plan is awaiting recovery; use the Farming Plan tab to review recovery options.");
            return;
        }
        var resumeCurrentWeek = plan is { State: "Stopped" } &&
                                plan.WeekStartUtc == FarmingSchedulePolicy.GetWeekStartUtc(DateTimeOffset.UtcNow);
        var territory = configuration.MainSelectedTerritory;
        if (resumeCurrentWeek)
            territory = plan!.TerritoryId;
        var dungeon = session.DungeonChoices.FirstOrDefault(choice => choice.Territory == territory) ??
                      (resumeCurrentWeek ? null : session.DungeonChoices.FirstOrDefault());
        var modeIndex = resumeCurrentWeek && Enum.TryParse<DebugTests.DutyModeSelection>(plan!.Mode, out var savedMode)
            ? (int)savedMode
            : Math.Clamp(configuration.MainSelectedDutyMode, 0, 1);
        var finishPeriods = resumeCurrentWeek ? plan!.RequestedPeriodCount : Math.Clamp(configuration.FarmingPeriodsToUse, 1, 7);
        var finishBoundary = FarmingSchedulePolicy.GetWeekStartUtc(DateTimeOffset.UtcNow).AddDays(finishPeriods);
        var snapshot = session.Dashboard;
        var selectedBareMode = resumeCurrentWeek ? plan!.BareMode : SingleRunPolicy.BareModeFor(configuration.EnableAutoDutyLoopActions);
        ImGui.TextWrapped($"Selected: {dungeon?.Name ?? "Unavailable"} · {(DebugTests.DutyModeSelection)modeIndex} · {finishPeriods} period(s)");
        TextDisabledWrapped($"Finish deadline: {finishBoundary.ToLocalTime():f}");
        var readinessMatches = resumeCurrentWeek
            ? snapshot is { LoggedIn: true } && snapshot.CharacterContentId == plan!.CharacterContentId && DateTimeOffset.UtcNow < plan.FinishDeadlineUtc
            : snapshot is { LoggedIn: true, CharacterContentId: not null } &&
              DateTimeOffset.UtcNow < finishBoundary &&
              snapshot.Eligibility?.Status == "Eligible" &&
              snapshot.SelectedTerritory == dungeon?.Territory &&
              snapshot.SelectedMode == (DebugTests.DutyModeSelection)modeIndex;
        ImGui.BeginDisabled(dungeon is null || !readinessMatches || !farmingPlan.CanStart || dungeonRun.Current is not null || retainerHandoff.OwnsActivity);
        if (ImGui.Button(resumeCurrentWeek ? "Resume farming…" : "Start farming…", new Vector2(-1, 0)))
        {
            confirmedFarmingDungeon = dungeon;
            confirmedFarmingMode = (DebugTests.DutyModeSelection)modeIndex;
            confirmedFarmingBareMode = selectedBareMode;
            confirmedFarmingPeriods = finishPeriods;
            confirmedServiceRetainers = configuration.MonitorRetainerVentures;
            confirmedRetainerWaitMinutes = Math.Clamp(configuration.RetainerWaitBeforeDungeonMinutes, 0, 30);
            confirmedIsResume = resumeCurrentWeek;
            confirmFarming = true;
            ImGui.OpenPopup("Confirm weekly tome farming plan");
        }
        ImGui.EndDisabled();
        if (dungeon is null)
            TextColoredWrapped(TomesTheme.Amber, "Cannot start: no dungeon is available yet.");
        else if (!farmingPlan.CanStart)
            TextColoredWrapped(TomesTheme.Amber, plan is { State: "Stopped" }
                ? "Cannot resume: this plan's finish deadline has passed."
                : "Cannot start while another plan is active or requires recovery.");
        else if (retainerHandoff.OwnsActivity)
            TextColoredWrapped(TomesTheme.Amber, "Cannot start while an AutoRetainer handoff owns character activity.");
        else if (dungeonRun.Current is not null)
            TextColoredWrapped(TomesTheme.Amber, "Cannot start while a dungeon operation is unresolved.");
        else if (!readinessMatches)
            TextDisabledWrapped("Start is waiting for a fresh Eligible snapshot for the selected dungeon and mode.");
        if (DateTimeOffset.UtcNow >= finishBoundary)
            TextColoredWrapped(TomesTheme.Amber, "Selected finish boundary has passed for this week.");
    }

    private void DrawRetainerPanel()
    {
        ImGui.Separator();
        ImGui.TextWrapped("RETAINER VENTURE HANDOFFS");
        var enabled = configuration.MonitorRetainerVentures;
        if (ImGui.Checkbox("Service ready ventures during farming", ref enabled))
        {
            configuration.MonitorRetainerVentures = enabled;
            try { saveConfiguration(); }
            catch (Exception error)
            {
                configuration.MonitorRetainerVentures = !enabled;
                actionMessage = $"Could not save retainer monitoring setting: {error.Message}";
            }
        }
        TextDisabledWrapped("Readiness scans are read-only. Venture-service permission is captured when farming starts.");
        var waitMinutes = Math.Clamp(configuration.RetainerWaitBeforeDungeonMinutes, 0, 30);
        ImGui.TextWrapped("Wait before next dungeon when venture is within (minutes)");
        ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.SliderInt("##RetainerWaitMinutes", ref waitMinutes, 0, 30))
        {
            var previous = configuration.RetainerWaitBeforeDungeonMinutes;
            configuration.RetainerWaitBeforeDungeonMinutes = waitMinutes;
            try { saveConfiguration(); }
            catch (Exception error)
            {
                configuration.RetainerWaitBeforeDungeonMinutes = previous;
                actionMessage = $"Could not save retainer wait setting: {error.Message}";
            }
        }
        TextDisabledWrapped("0 waits only for ventures already ready; 30 waits up to half an hour. Active plans use the value captured at confirmation.");
        var snapshot = configuration.MonitorRetainerVentures ? readRetainerReadiness() : null;
        if (!configuration.MonitorRetainerVentures)
            TextDisabledWrapped("Enable monitoring to scan readiness. This setting does not change an already confirmed plan.");
        else if (snapshot is null)
            TextDisabledWrapped("Waiting for the first AutoRetainer readiness scan…");
        else if (!snapshot.Known)
            TextColoredWrapped(TomesTheme.Amber, $"Readiness unknown: {snapshot.Error}");
        else
        {
            ImGui.TextWrapped($"AutoRetainer characters: {snapshot.RegisteredCharacters} · eligible for pass: {snapshot.ReadyCharacters} (current character excluded)");
            ImGui.TextWrapped(snapshot.ClosestEligibleSeconds is { } seconds
                ? $"Closest selected venture: {TimeSpan.FromSeconds(Math.Max(0, seconds)):g}"
                : "No eligible venture countdown reported.");
            if (snapshot.Busy is { } busy && snapshot.MultiModeActive is { } active)
                ImGui.TextWrapped($"AutoRetainer: {(busy ? "busy" : "idle")} · Multi Mode {(active ? "active" : "inactive")}");
            else
                TextColoredWrapped(TomesTheme.Amber, "AutoRetainer activity state is unknown.");
            TextDisabledWrapped($"Read at {snapshot.CapturedUtc.ToLocalTime():t}");
        }

        var automationSession = retainerHandoff.Session;
        if (automationSession is { State: "Active" or "StopRequested" })
        {
            ImGui.Separator();
            ImGui.TextWrapped("LINKED FARMING SESSION");
            TextColoredWrapped(TomesTheme.ForStatus(automationSession.State), $"{automationSession.State} · {automationSession.FarmingCharacterName}@{automationSession.FarmingCharacterWorld}");
            ImGui.TextWrapped(automationSession.Detail);
            TextDisabledWrapped($"Venture service captured: {(automationSession.ServiceRetainers ? "enabled" : "disabled")} · expires {automationSession.ExpiresUtc.ToLocalTime():f}");
        }
        if (retainerHandoff.NeedsRecovery)
        {
            TextColoredWrapped(TomesTheme.Rose, "Recovery required; no request will be replayed.");
            ImGui.TextWrapped(automationSession?.Detail ?? retainerHandoff.Handoff?.Detail ?? "Inspect provider and character state.");
            if (ImGui.Button("Acknowledge recovery and stop combined flow", new Vector2(-1, 0)) &&
                !retainerHandoff.AcknowledgeRecoveryAndStop())
                actionMessage = "Recovery acknowledgement blocked: verify the farming character, AutoRetainer, Lifestream, and AutoDuty are idle and the player is safe.";
        }

        if (retainerHandoff.Handoff is { } handoff)
        {
            TextColoredWrapped(TomesTheme.ForStatus(handoff.State), $"Handoff: {handoff.State}");
            ImGui.TextWrapped(handoff.Detail);
        }
        if (!string.IsNullOrWhiteSpace(actionMessage))
            TextColoredWrapped(TomesTheme.Amber, actionMessage);
    }

    private void DrawFarmingConfirmation()
    {
        if (!confirmFarming || !ImGui.BeginPopupModal("Confirm weekly tome farming plan", ref confirmFarming,
                ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var dungeon = confirmedFarmingDungeon!;
        var snapshot = session.Dashboard;
        var remaining = snapshot?.WeeklyAcquired is { } acquired && snapshot.WeeklyLimit is { } limit
            ? Math.Max(0, limit - acquired)
            : (int?)null;
        var runs = remaining is { } amount ? (int)Math.Ceiling(amount / (double)dungeon.Reward) : (int?)null;
        var serviceRetainers = confirmedServiceRetainers;
        var weekStart = FarmingSchedulePolicy.GetWeekStartUtc(DateTimeOffset.UtcNow);
        var finishBoundary = weekStart.AddDays(confirmedFarmingPeriods);
        ImGui.TextWrapped($"Dungeon: {dungeon.Name}\nMode: {confirmedFarmingMode}\nExpected reward: {dungeon.Reward}\nPeriods: {confirmedFarmingPeriods}\nEstimated runs: {runs?.ToString() ?? "unavailable"}\nFinish deadline: {FarmingSchedulePolicy.GetWeekStartUtc(DateTimeOffset.UtcNow).AddDays(confirmedFarmingPeriods).ToLocalTime():f}\nbareMode: {confirmedFarmingBareMode}");
        ImGui.TextWrapped($"Retainer service: {(serviceRetainers ? "enabled" : "disabled")} · near-complete wait: {confirmedRetainerWaitMinutes} minute(s).");
        if (confirmedIsResume && farmingPlan.Current is { } priorPlan)
        {
            var period = priorPlan.CurrentPeriodStartUtc is { } start
                ? priorPlan.Periods.FirstOrDefault(item => item.StartUtc == start)
                : null;
            ImGui.TextWrapped($"Resume plan {priorPlan.PlanId}: preserving {priorPlan.CreditedOperationIds.Count} verified runs and current-period progress {period?.CompletedRuns ?? 0}/{period?.AllocatedRuns ?? 0}. Retainer settings will update only after confirmation.");
            if (period is not null && period.CompletedRuns >= period.AllocatedRuns)
                ImGui.TextWrapped("This period is complete: no additional dungeon runs will launch before the next period; enabled retainer servicing may continue.");
        }
        if (runs is { } count && DateTimeOffset.UtcNow < finishBoundary)
        {
            var firstPeriod = FarmingSchedulePolicy.GetPeriodStartUtc(DateTimeOffset.UtcNow, weekStart);
            var preview = FarmingSchedulePolicy.CreatePeriods(firstPeriod, finishBoundary, count);
            ImGui.TextWrapped("Run allocation: " + string.Join(" · ", preview.Select(period =>
                $"{period.StartUtc.ToLocalTime():ddd HH:mm} {period.AllocatedRuns}")));
        }
        else if (DateTimeOffset.UtcNow >= finishBoundary)
            TextColoredWrapped(TomesTheme.Amber, "Finish boundary already passed; plan cannot start this week.");
        ImGui.TextWrapped("TT will distribute runs across reset-aligned 08:00 UTC periods, validate before every launch, and continue only after verified reward and safe exit. After each verified dungeon, TT checks ventures and may complete one Retainers-only pass and return before the next dungeon. Stop prevents new work; owned activity and return cleanup settle safely.");
        ImGui.TextWrapped("AutoDuty ownership is exclusive by user convention: keep other integrations from controlling it or pushing overrides until TT releases each run.");
        if (!confirmedFarmingBareMode)
            TextColoredWrapped(TomesTheme.Amber, "Warning: configured AutoDuty termination actions may log out or close the client.");
        ImGui.Separator();
        if (ImGui.Button(confirmedIsResume ? "Confirm resume" : "Confirm farming plan"))
        {
            try
            {
                var contentId = snapshot?.CharacterContentId;
                if (contentId is null or 0)
                    throw new InvalidOperationException("Character identity unavailable in cached snapshot.");
                farmingPlan.QueueStart(confirmedFarmingDungeon!, confirmedFarmingMode, -1,
                    confirmedFarmingBareMode, confirmedFarmingPeriods, contentId.Value,
                    snapshot!.CharacterName, snapshot.CharacterHomeWorld,
                    confirmedServiceRetainers, confirmedRetainerWaitMinutes);
                actionMessage = "Plan confirmed; fresh validation queued on framework thread.";
            }
            catch (Exception error) { actionMessage = error.Message; }
            confirmFarming = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            confirmFarming = false;
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
