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
    private readonly AutomationSessionController automationSession;
    private readonly OceanFishingHandoffController oceanFishing;
    private readonly Func<(bool Known, IReadOnlyList<RetainerCharacterIdentity> Characters)> readRetainerRoster;
    private bool confirmFarming;
    private DebugTests.DungeonOption? confirmedFarmingDungeon;
    private DebugTests.DutyModeSelection confirmedFarmingMode;
    private bool confirmedFarmingBareMode;
    private int confirmedFarmingPeriods;
    private bool confirmedServiceRetainers;
    private int confirmedRetainerWaitMinutes;
    private bool confirmedIsResume;
    private bool confirmedProviderOnly;
    private string? actionMessage;

    public bool AliasAvailable { get; set; }

    public MainWindow(DebugSession session, Configuration configuration, DungeonRunController dungeonRun,
        FarmingPlanController farmingPlan,
        Action saveConfiguration, Action openDiagnostics, IPluginLog log,
        Func<RetainerReadinessSnapshot?> readRetainerReadiness, RetainerHandoffController retainerHandoff,
        AutomationSessionController automationSession, OceanFishingHandoffController oceanFishing,
        Func<(bool Known, IReadOnlyList<RetainerCharacterIdentity> Characters)> readRetainerRoster)
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
        this.automationSession = automationSession;
        this.oceanFishing = oceanFishing;
        this.readRetainerRoster = readRetainerRoster;
        TomesTheme.SetPreset(configuration.ThemePreset);
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
            if (ImGui.BeginTabItem("Ocean Fishing"))
            {
                DrawOceanFishingPanel();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsPanel();
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
            else if (plan.State == "RecoveryRequired")
            {
                var canResume = farmingPlan.CanResumeAfterReload && retainerHandoff.CanResumeAutomationSession &&
                                oceanFishing.CanResumeAutomationSession;
                if (canResume && ImGui.Button("Acknowledge reload and resume plan", new Vector2(-1, 0)))
                {
                    if (!farmingPlan.ResumeAfterReload(retainerHandoff.CanResumeAutomationSession &&
                                                       oceanFishing.CanResumeAutomationSession))
                        actionMessage = "Resume was not persisted. Automation remains blocked; verify the farming character and idle providers, then try again.";
                    else
                        actionMessage = "Captured automation session resumed. Fishing settings, handled windows, and verified plan progress are preserved.";
                }
                if (!canResume)
                    TextColoredWrapped(TomesTheme.Amber, "Resume is blocked until the farming character is safe, AutoDuty and providers are idle, and no fishing or retainer handoff needs recovery.");

                var parentStopped = automationSession.Current is null or { State: "Stopped" };
                var automationSafe = parentStopped || retainerHandoff.CanResumeAutomationSession && oceanFishing.CanResumeAutomationSession;
                var canAcknowledgeStop = farmingPlan.CanAcknowledgeRecovery && automationSafe;
                ImGui.BeginDisabled(!canAcknowledgeStop);
                if (ImGui.Button("Acknowledge recovery and stop automation", new Vector2(-1, 0)))
                {
                    if (!farmingPlan.AcknowledgeRecoveryAndStop(automationSafe))
                        actionMessage = "Recovery was not cleared. Safely resolve any owned fishing/retainer activity and dungeon run first.";
                    else
                        actionMessage = "Recovery acknowledged and automation stopped. Start a new session explicitly when ready.";
                }
                ImGui.EndDisabled();
                if (!farmingPlan.CanAcknowledgeRecovery)
                    TextDisabledWrapped("Stop is unavailable until the linked dungeon run has been safely acknowledged.");
                else if (!automationSafe)
                    TextDisabledWrapped("Stop is waiting for safe farming-character identity and idle providers. Resolve fishing or retainer recovery in its tab first.");
            }
            else if (farmingPlan.CanAcknowledgeRecovery && ImGui.Button("Acknowledge recovery and stop automation", new Vector2(-1, 0)))
            {
                var automationSafe = automationSession.Current is null or { State: "Stopped" } ||
                                     retainerHandoff.CanResumeAutomationSession && oceanFishing.CanResumeAutomationSession;
                if (!farmingPlan.AcknowledgeRecoveryAndStop(automationSafe))
                    actionMessage = "Recovery was not cleared. Safely resolve any owned provider activity and dungeon run first.";
            }
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
        if (automationSession.Current is { State: "Active" or "StopRequested" } activeSession)
        {
            TextColoredWrapped(TomesTheme.ForStatus(activeSession.State), $"Automation session: {activeSession.State}");
            ImGui.TextWrapped(activeSession.Detail);
            if (activeSession.State == "Active" && ImGui.Button("Stop automation", new Vector2(-1, 0)))
                retainerHandoff.RequestStop();
            return;
        }
        var resumeCurrentWeek = plan is { State: "Stopped" } &&
                                plan.WeekStartUtc == FarmingSchedulePolicy.GetWeekStartUtc(DateTimeOffset.UtcNow);
        var resumePlan = resumeCurrentWeek && DateTimeOffset.UtcNow < plan!.FinishDeadlineUtc;
        var territory = configuration.MainSelectedTerritory;
        if (resumePlan)
            territory = plan!.TerritoryId;
        var dungeon = session.DungeonChoices.FirstOrDefault(choice => choice.Territory == territory) ??
                      (resumePlan ? null : session.DungeonChoices.FirstOrDefault());
        var modeIndex = resumePlan && Enum.TryParse<DebugTests.DutyModeSelection>(plan!.Mode, out var savedMode)
            ? (int)savedMode
            : Math.Clamp(configuration.MainSelectedDutyMode, 0, 1);
        var finishPeriods = resumePlan ? plan!.RequestedPeriodCount : Math.Clamp(configuration.FarmingPeriodsToUse, 1, 7);
        var finishBoundary = FarmingSchedulePolicy.GetWeekStartUtc(DateTimeOffset.UtcNow).AddDays(finishPeriods);
        var snapshot = session.Dashboard;
        var selectedBareMode = resumePlan ? plan!.BareMode : SingleRunPolicy.BareModeFor(configuration.EnableAutoDutyLoopActions);
        var readinessMatches = resumePlan
            ? snapshot is { LoggedIn: true } && snapshot.CharacterContentId == plan!.CharacterContentId && DateTimeOffset.UtcNow < plan.FinishDeadlineUtc
            : snapshot is { LoggedIn: true, CharacterContentId: not null } &&
              DateTimeOffset.UtcNow < finishBoundary &&
              snapshot.Eligibility?.Status == "Eligible" &&
              snapshot.SelectedTerritory == dungeon?.Territory &&
              snapshot.SelectedMode == (DebugTests.DutyModeSelection)modeIndex;
        var canStartProvidersOnly = snapshot is { LoggedIn: true, CharacterContentId: not null } &&
                                    (configuration.OceanFishingEnabled || configuration.MonitorRetainerVentures) &&
                                    (configuration.OceanFishingCharacterMode != "SelectedCharacter" ||
                                     configuration.OceanFishingCharacterContentId != 0 &&
                                     !string.IsNullOrWhiteSpace(configuration.OceanFishingCharacterName) &&
                                     !string.IsNullOrWhiteSpace(configuration.OceanFishingCharacterWorld)) &&
                                    (plan is null || plan.State is "Stopped" or "Expired" or "RecoveryAcknowledged") &&
                                    (automationSession.Current is null or { State: "Stopped" });
        var canStartDungeonPlan = dungeon is not null && readinessMatches && farmingPlan.CanStart;
        var canStartFullFlow = (canStartDungeonPlan || canStartProvidersOnly) &&
                               dungeonRun.Current is null && !retainerHandoff.OwnsActivity && !oceanFishing.OwnsActivity;
        var willResumePlan = resumePlan && canStartDungeonPlan;
        ImGui.TextWrapped(willResumePlan
            ? $"Resume weekly plan: {dungeon?.Name ?? "Unavailable"} · {(DebugTests.DutyModeSelection)modeIndex} · {finishPeriods} period(s)"
            : canStartDungeonPlan
                ? $"Dungeon plan: {dungeon!.Name} · {(DebugTests.DutyModeSelection)modeIndex} · {finishPeriods} period(s)"
                : "Provider automation: Ocean Fishing and/or retainers; no dungeon plan will be started.");
        if (canStartDungeonPlan || willResumePlan)
            TextDisabledWrapped($"Finish deadline: {finishBoundary.ToLocalTime():f}");
        ImGui.BeginDisabled(!canStartFullFlow);
        if (ImGui.Button(willResumePlan ? "Resume full automation…" : "Start full automation…", new Vector2(-1, 0)))
        {
            confirmedProviderOnly = !canStartDungeonPlan;
            confirmedFarmingDungeon = confirmedProviderOnly ? null : dungeon;
            confirmedFarmingMode = (DebugTests.DutyModeSelection)modeIndex;
            confirmedFarmingBareMode = selectedBareMode;
            confirmedFarmingPeriods = finishPeriods;
            confirmedServiceRetainers = configuration.MonitorRetainerVentures;
            confirmedRetainerWaitMinutes = Math.Clamp(configuration.RetainerWaitBeforeDungeonMinutes, 0, 30);
            confirmedIsResume = willResumePlan;
            confirmFarming = true;
            ImGui.OpenPopup("Confirm full automation");
        }
        ImGui.EndDisabled();
        if (!configuration.OceanFishingEnabled && !configuration.MonitorRetainerVentures && !canStartDungeonPlan && !resumePlan)
            TextColoredWrapped(TomesTheme.Amber, "Enable Ocean Fishing, retainer service, or a ready dungeon plan to start automation.");
        else if (resumeCurrentWeek && !resumePlan && !canStartProvidersOnly)
            TextColoredWrapped(TomesTheme.Amber, "The weekly plan deadline passed. A provider-only session can still be started when Ocean Fishing or retainer service is enabled.");
        else if (!canStartDungeonPlan && !canStartProvidersOnly && !resumePlan)
            TextDisabledWrapped("Dungeon farming is unavailable or capped. Provider automation can still run through weekly reset when enabled.");
        if (!farmingPlan.CanStart && !canStartProvidersOnly && !resumePlan)
            TextColoredWrapped(TomesTheme.Amber, plan is { State: "Stopped" }
                ? "Cannot start another dungeon plan while this week's stopped plan is still resumable."
                : "Cannot start while another plan is active or requires recovery.");
        if (canStartDungeonPlan && DateTimeOffset.UtcNow >= finishBoundary)
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
        TextDisabledWrapped("Readiness scans are read-only. Venture-service permission is captured when the automation session starts.");
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
        TextDisabledWrapped("0 waits only for ventures already ready; 30 waits up to half an hour. Active sessions use the value captured at confirmation.");
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

    private void DrawOceanFishingPanel()
    {
        ImGui.Separator();
        ImGui.TextWrapped("OCEAN FISHING HANDOFF");
        ImGui.TextWrapped("Priority is Ocean Fishing → retainers → dungeon. WahTools manages route selection and movement; TT selects the character, requests one voyage per window, observes cleanup, and returns to the farming character.");
        TextDisabledWrapped("Start and confirm Ocean Fishing as part of full automation from Current Character. Fishing can run without a dungeon plan and continues after cap/reset until Stop.");
        var enabled = configuration.OceanFishingEnabled;
        if (ImGui.Checkbox("Enable Ocean Fishing in the next automation session", ref enabled))
            SavePreference(() => configuration.OceanFishingEnabled = enabled, () => configuration.OceanFishingEnabled = !enabled,
                "Ocean Fishing preference");

        var lead = Math.Clamp(configuration.OceanFishingPreparationLeadMinutes, 0, 45);
        ImGui.TextWrapped("Delay other work from this Ocean Fishing preparation lead (minutes)");
        ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.SliderInt("##OceanFishingLead", ref lead, 0, 45))
        {
            var previous = configuration.OceanFishingPreparationLeadMinutes;
            configuration.OceanFishingPreparationLeadMinutes = lead;
            try { saveConfiguration(); }
            catch (Exception error)
            {
                configuration.OceanFishingPreparationLeadMinutes = previous;
                actionMessage = $"Could not save Ocean Fishing preparation lead: {error.Message}";
            }
        }
        TextDisabledWrapped("Default 15 minutes; maximum 45. Registration occurs every even UTC hour. TT stops attempting a launch at minute 13; a missed window is skipped.");

        var mode = configuration.OceanFishingCharacterMode == "SelectedCharacter" ? 1 : 0;
        var modes = new[] { "Current farming character", "Selected AutoRetainer character" };
        ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.Combo("Fishing character mode", ref mode, modes, modes.Length))
        {
            var previous = configuration.OceanFishingCharacterMode;
            configuration.OceanFishingCharacterMode = mode == 0 ? "CurrentCharacter" : "SelectedCharacter";
            try { saveConfiguration(); }
            catch (Exception error)
            {
                configuration.OceanFishingCharacterMode = previous;
                actionMessage = $"Could not save fishing character mode: {error.Message}";
            }
        }
        if (configuration.OceanFishingCharacterMode == "SelectedCharacter")
        {
            var roster = readRetainerRoster();
            if (!roster.Known)
                TextColoredWrapped(TomesTheme.Amber, "AutoRetainer character roster unavailable; selected-character fishing is paused.");
            else if (roster.Characters.Count == 0)
                TextColoredWrapped(TomesTheme.Amber, "No registered characters were returned by AutoRetainer.");
            else
            {
                var selected = roster.Characters.FirstOrDefault(character => character.ContentId == configuration.OceanFishingCharacterContentId);
                var label = selected is null ? "Select registered character" : $"{selected.Name}@{selected.World}";
                ImGui.SetNextItemWidth(Math.Min(300f * TomesTheme.Scale, ImGui.GetContentRegionAvail().X));
                if (ImGui.BeginCombo("Fishing character", label))
                {
                    foreach (var character in roster.Characters)
                    {
                        if (!ImGui.Selectable($"{character.Name}@{character.World}##fisher-{character.ContentId}",
                                character.ContentId == configuration.OceanFishingCharacterContentId))
                            continue;
                        var oldId = configuration.OceanFishingCharacterContentId;
                        var oldName = configuration.OceanFishingCharacterName;
                        var oldWorld = configuration.OceanFishingCharacterWorld;
                        configuration.OceanFishingCharacterContentId = character.ContentId;
                        configuration.OceanFishingCharacterName = character.Name;
                        configuration.OceanFishingCharacterWorld = character.World;
                        try { saveConfiguration(); }
                        catch (Exception error)
                        {
                            configuration.OceanFishingCharacterContentId = oldId;
                            configuration.OceanFishingCharacterName = oldName;
                            configuration.OceanFishingCharacterWorld = oldWorld;
                            actionMessage = $"Could not save fishing character selection: {error.Message}";
                        }
                    }
                    ImGui.EndCombo();
                }
            }
        }

        if (oceanFishing.Current is { } operation)
        {
            TextColoredWrapped(TomesTheme.ForStatus(operation.State), $"Window {operation.WindowStartUtc.ToLocalTime():f} · {operation.State}");
            ImGui.TextWrapped(operation.Detail);
        }
        else
            TextDisabledWrapped($"WahTools: {(configuration.AutomationSession is { OceanFishingEnabled: true } ? "waiting for eligible window" : "not authorized in current session")}");

        if (oceanFishing.Admission is { } admission)
        {
            ImGui.Separator();
            var diagnosticColor = admission.Code == "AdmissionReady" ? TomesTheme.Mint :
                admission.Code.Contains("Recovery", StringComparison.Ordinal) ? TomesTheme.Rose : TomesTheme.Amber;
            TextColoredWrapped(diagnosticColor,
                $"Admission: {admission.Code}");
            ImGui.TextWrapped(admission.Detail);
            if (admission.WindowStartUtc is { } start && admission.LaunchCutoffUtc is { } cutoff)
                TextDisabledWrapped($"Window: {start.ToLocalTime():f} · launch cutoff: {cutoff.ToLocalTime():t}");
            if (admission.AutoDutyStopped is not null || admission.AutoRetainerBusy is not null ||
                admission.LifestreamBusy is not null || admission.WahToolsAvailable is not null)
                TextDisabledWrapped($"Player safe: {admission.PlayerSafe?.ToString() ?? "unknown"} · AutoDuty stopped: {admission.AutoDutyStopped?.ToString() ?? "unknown"} · AutoRetainer busy/Multi Mode: {admission.AutoRetainerBusy?.ToString() ?? "unknown"}/{admission.AutoRetainerMultiMode?.ToString() ?? "unknown"} · Lifestream busy: {admission.LifestreamBusy?.ToString() ?? "unknown"} · WahTools available/Feesh clean: {admission.WahToolsAvailable?.ToString() ?? "unknown"}/{admission.FeeshClean?.ToString() ?? "unknown"}");
            if (!string.IsNullOrWhiteSpace(admission.FeeshDetail))
                TextDisabledWrapped($"Feesh: {admission.FeeshDetail}");
        }

    }

    private void SavePreference(Action apply, Action rollback, string description)
    {
        apply();
        try { saveConfiguration(); }
        catch (Exception error)
        {
            rollback();
            actionMessage = $"Could not save {description}: {error.Message}";
        }
    }

    private void DrawSettingsPanel()
    {
        ImGui.Separator();
        ImGui.TextWrapped("SETTINGS");
        ImGui.TextWrapped("Choose a coordinated accent and window palette. This selection applies to the main and diagnostics windows.");
        var presetIndex = Array.IndexOf(TomesTheme.PresetNames, TomesTheme.CurrentPreset);
        if (presetIndex < 0)
            presetIndex = 0;
        ImGui.SetNextItemWidth(300f * TomesTheme.Scale);
        if (ImGui.Combo("Color theme", ref presetIndex, TomesTheme.PresetNames, TomesTheme.PresetNames.Length))
        {
            var previous = configuration.ThemePreset;
            configuration.ThemePreset = TomesTheme.PresetNames[presetIndex];
            TomesTheme.SetPreset(configuration.ThemePreset);
            try { saveConfiguration(); }
            catch (Exception error)
            {
                configuration.ThemePreset = previous;
                TomesTheme.SetPreset(previous);
                actionMessage = $"Could not save color theme: {error.Message}";
            }
        }
        ImGui.Spacing();
        ImGui.TextWrapped($"Selected: {TomesTheme.CurrentPreset}");
        TextColoredWrapped(TomesTheme.Cyan, "Accent preview");
        ImGui.TextWrapped("Window background preview");
        ImGui.PushStyleColor(ImGuiCol.ChildBg, TomesTheme.Window);
        if (ImGui.BeginChild("WindowColorPreview", new Vector2(300f * TomesTheme.Scale, 90f * TomesTheme.Scale), true))
        {
            TextColoredWrapped(TomesTheme.Cyan, "Selected controls and highlights");
            TextColoredWrapped(TomesTheme.Text, "Readable foreground text");
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        TextDisabledWrapped("Fiery Ocean, Dark Sunset, and Summer Fun coordinate window, panel, border, and accent colors. Warning, success, and error colors remain distinct.");
        if (!string.IsNullOrWhiteSpace(actionMessage))
            TextColoredWrapped(TomesTheme.Amber, actionMessage);
    }

    private void DrawFarmingConfirmation()
    {
        if (!confirmFarming || !ImGui.BeginPopupModal("Confirm full automation", ref confirmFarming,
                ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var dungeon = confirmedFarmingDungeon;
        var snapshot = session.Dashboard;
        var remaining = dungeon is not null && snapshot?.WeeklyAcquired is { } acquired && snapshot.WeeklyLimit is { } limit
            ? Math.Max(0, limit - acquired)
            : (int?)null;
        var runs = remaining is { } amount && dungeon is not null ? (int)Math.Ceiling(amount / (double)dungeon.Reward) : (int?)null;
        var serviceRetainers = confirmedServiceRetainers;
        var weekStart = FarmingSchedulePolicy.GetWeekStartUtc(DateTimeOffset.UtcNow);
        var finishBoundary = weekStart.AddDays(confirmedFarmingPeriods);
        if (confirmedProviderOnly)
        {
            ImGui.TextWrapped("No weekly dungeon plan will be created. This session can service retainers and/or run Ocean Fishing until you press Stop.");
            if (snapshot?.WeeklyAcquired is { } capped && snapshot.WeeklyLimit is { } weeklyLimit && capped >= weeklyLimit)
                TextColoredWrapped(TomesTheme.Amber, "Weekly tome cap reached. Fishing and retainer automation remain enabled and can continue through the weekly reset.");
            else
                TextDisabledWrapped("Dungeon farming is unavailable or not selected. Provider automation will continue independently until Stop.");
        }
        else if (dungeon is not null)
        {
            ImGui.TextWrapped($"Dungeon: {dungeon.Name}\nMode: {confirmedFarmingMode}\nExpected reward: {dungeon.Reward}\nPeriods: {confirmedFarmingPeriods}\nEstimated runs: {runs?.ToString() ?? "unavailable"}\nFinish deadline: {finishBoundary.ToLocalTime():f}\nbareMode: {confirmedFarmingBareMode}");
        }
        ImGui.TextWrapped($"Ocean Fishing: {(configuration.OceanFishingEnabled ? "enabled" : "disabled")} · preparation lead {Math.Clamp(configuration.OceanFishingPreparationLeadMinutes, 0, 45)} minutes · character {(configuration.OceanFishingCharacterMode == "CurrentCharacter" ? "farming character" : $"{configuration.OceanFishingCharacterName}@{configuration.OceanFishingCharacterWorld}")}.");
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
        if (!confirmedProviderOnly && runs is { } count && DateTimeOffset.UtcNow < finishBoundary)
        {
            var firstPeriod = FarmingSchedulePolicy.GetPeriodStartUtc(DateTimeOffset.UtcNow, weekStart);
            var preview = FarmingSchedulePolicy.CreatePeriods(firstPeriod, finishBoundary, count);
            ImGui.TextWrapped("Run allocation: " + string.Join(" · ", preview.Select(period =>
                $"{period.StartUtc.ToLocalTime():ddd HH:mm} {period.AllocatedRuns}")));
        }
        else if (!confirmedProviderOnly && DateTimeOffset.UtcNow >= finishBoundary)
            TextColoredWrapped(TomesTheme.Amber, "Finish boundary already passed; plan cannot start this week.");
        if (confirmedProviderOnly)
            ImGui.TextWrapped("Priority is Ocean Fishing → retainers → dungeon. This session has no dungeon plan; weekly reset will not create one automatically. Start a new dungeon plan explicitly when ready. Stop prevents new work and lets owned activity settle safely.");
        else
        {
            ImGui.TextWrapped("TT will distribute runs across reset-aligned 08:00 UTC periods, validate before every launch, and continue only after verified reward and safe exit. After each verified dungeon, TT checks ventures and may complete one Retainers-only pass and return before the next dungeon. Stop prevents new work; owned activity and return cleanup settle safely.");
            ImGui.TextWrapped("AutoDuty ownership is exclusive by user convention: keep other integrations from controlling it or pushing overrides until TT releases each run.");
        }
        if (!confirmedProviderOnly && !confirmedFarmingBareMode)
            TextColoredWrapped(TomesTheme.Amber, "Warning: configured AutoDuty termination actions may log out or close the client.");
        ImGui.Separator();
        if (ImGui.Button(confirmedProviderOnly ? "Confirm automation" : confirmedIsResume ? "Confirm resume" : "Confirm full automation"))
        {
            try
            {
                var contentId = snapshot?.CharacterContentId;
                if (contentId is null or 0)
                    throw new InvalidOperationException("Character identity unavailable in cached snapshot.");
                if (confirmedProviderOnly)
                {
                    var linkedPlan = farmingPlan.Current is { State: "Active" } activePlan ? activePlan.PlanId : null;
                    automationSession.StartOrResume(contentId.Value, snapshot!.CharacterName, snapshot.CharacterHomeWorld,
                        serviceRetainers, confirmedRetainerWaitMinutes, configuration.OceanFishingEnabled,
                        Math.Clamp(configuration.OceanFishingPreparationLeadMinutes, 0, 45),
                        configuration.OceanFishingCharacterMode, configuration.OceanFishingCharacterContentId,
                        configuration.OceanFishingCharacterName, configuration.OceanFishingCharacterWorld, linkedPlan);
                    actionMessage = "Full automation session confirmed. Fishing and retainers remain active until Stop; no dungeon plan was created.";
                }
                else
                {
                    if (dungeon is null)
                        throw new InvalidOperationException("Dungeon selection is unavailable.");
                    farmingPlan.QueueStart(dungeon, confirmedFarmingMode, -1,
                        confirmedFarmingBareMode, confirmedFarmingPeriods, contentId.Value,
                        snapshot!.CharacterName, snapshot.CharacterHomeWorld,
                        confirmedServiceRetainers, confirmedRetainerWaitMinutes);
                    actionMessage = "Full automation confirmed; fresh dungeon validation queued on framework thread.";
                }
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
