using Dalamud.Plugin.Services;
using System.Text.RegularExpressions;

namespace TomesAndTides.Debugging;

public sealed record DebugReport(string Status, string Text);
public sealed record DebugResult(DateTimeOffset CapturedUtc, DebugReport Report, string? SelectionFingerprint = null);
public sealed record DebugTest(string Name, string Description, Func<DebugReport> Run);

/// <summary>Executes at most one requested read-only test per framework update.</summary>
public sealed class DebugSession(DebugTests tests, IPluginLog log)
{
    private readonly Queue<int> pending = new();
    private readonly Dictionary<int, DebugResult> results = new();
    private string currentEligibilityFingerprint = string.Empty;
    private DashboardSnapshot? dashboardSnapshot;

    public IReadOnlyList<DebugTest> Tests { get; } = tests.CreateTests();
    public IReadOnlyList<DebugTests.DungeonOption> DungeonChoices => tests.DungeonChoices;
    public IReadOnlyList<DebugTests.GearsetOption> GearsetChoices => tests.GearsetChoices;
    public bool HasPending => pending.Count > 0;
    public DashboardSnapshot? Dashboard => dashboardSnapshot;
    public DebugResult? GetResult(int index) => results.GetValueOrDefault(index);

    public void SelectEligibility(uint territoryId, DebugTests.DutyModeSelection mode) =>
        tests.SelectEligibility(territoryId, mode);

    public uint SelectedTerritory => tests.SelectedTerritory;
    public DebugTests.DutyModeSelection SelectedMode => tests.SelectedMode;
    public int SelectedGearsetId => tests.SelectedGearsetId;
    public void SelectGearset(int id) => tests.SelectGearset(id);
    public string CurrentEligibilityFingerprint => currentEligibilityFingerprint;
    public DebugReport CheckEligibility(uint territoryId, DebugTests.DutyModeSelection mode, int gearsetId) =>
        tests.CheckEligibility(territoryId, mode, gearsetId);
    public void Queue(int index)
    {
        if (index >= 0 && index < Tests.Count && !pending.Contains(index))
        {
            pending.Enqueue(index);
            log.Debug("Queued diagnostic test {Test}.", Tests[index].Name);
        }
    }

    public void QueueAll()
    {
        for (var index = 0; index < Tests.Count; index++)
            Queue(index);
    }

    public void Clear()
    {
        pending.Clear();
        results.Clear();
    }

    public void RefreshDashboardSnapshot(uint selectedTerritory, DebugTests.DutyModeSelection mode)
    {
        try
        {
            dashboardSnapshot = tests.ReadDashboardSnapshot(selectedTerritory, mode);
        }
        catch (Exception error)
        {
            dashboardSnapshot = new DashboardSnapshot(DateTimeOffset.UtcNow, false, string.Empty,
                string.Empty, null, null, null, null, selectedTerritory, mode, null,
                error.GetBaseException().Message);
            log.Debug(error, "Main-window snapshot refresh failed; unavailable values remain unknown.");
        }
    }

    public void Tick()
    {
        currentEligibilityFingerprint = tests.EligibilityFingerprint;
        if (!pending.TryDequeue(out var index))
            return;
        var startedUtc = DateTimeOffset.UtcNow;
        log.Debug("Starting diagnostic test {Test} at {StartedUtc}.", Tests[index].Name, startedUtc);
        try
        {
            var report = Tests[index].Run();
            var capturedUtc = DateTimeOffset.UtcNow;
            results[index] = new DebugResult(capturedUtc, report,
                Tests[index].Name is "Dungeon eligibility" or "AutoDuty launch readiness"
                    ? tests.EligibilityFingerprint
                    : null);
            log.Information("Diagnostic test {Test} finished with status {Status} at {CapturedUtc}.",
                Tests[index].Name, report.Status, capturedUtc);
            var sanitizedReport = Regex.Replace(report.Text, @"(?i)Content ID\s+\d+", "Content ID <redacted>");
            log.Debug("Diagnostic report for {Test}:\n{Report}", Tests[index].Name, sanitizedReport);
        }
        catch (Exception error)
        {
            log.Error(error, "Diagnostic test {Test} failed.", Tests[index].Name);
            results[index] = new DebugResult(DateTimeOffset.UtcNow,
                new DebugReport("Failed", error.ToString()));
        }
    }
}
