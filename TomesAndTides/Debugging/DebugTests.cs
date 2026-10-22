using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using TomesAndTides.Integrations;

namespace TomesAndTides.Debugging;

/// <summary>Game and IPC reads only. Called exclusively from framework updates.</summary>
public sealed class DebugTests(
    IDataManager data, IClientState client, IObjectTable objects,
    IDalamudPluginInterface pluginInterface, IUnlockState unlocks,
    IPlayerState playerState, ICondition conditions, Configuration configuration,
    WahToolsFeeshStatusReader feeshStatusReader)
{
    // FFXIV's current tomestone holding rule is 2,000 for each type.
    // Unlike the weekly cap, the holding cap is not represented in the exposed Lumina sheets.
    private const int TomestoneHoldingLimit = 2000;
    public enum DutyModeSelection { Support, Trust }

    public sealed record DungeonOption(uint Territory, uint ContentFinderConditionId, uint Instance, string Name, int Reward,
        int RequiredLevel, int RequiredItemLevel, bool Support, bool Trust);
    public sealed record GearsetOption(int Id, string Name, byte ClassJob, string JobName,
        bool CombatJob, short ItemLevel);
    public sealed record LiveTomeState(ulong CharacterContentId, uint CurrencyItemId,
        int WeeklyAcquired, int WeeklyLimit, ulong HeldBalance);

    public unsafe LiveTomeState ReadLiveTomeState()
    {
        if (!client.IsLoggedIn || playerState.ContentId == 0)
            throw new InvalidOperationException("Character is not logged in or identity is unavailable.");
        var currency = ResolveCurrency();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            throw new InvalidOperationException("InventoryManager is unavailable.");
        var weeklyAcquired = inventory->GetWeeklyAcquiredTomestoneCount();
        var weeklyLimit = InventoryManager.GetLimitedTomestoneWeeklyLimit();
        var held = inventory->GetTomestoneCount(currency.ItemId);
        if (weeklyAcquired < 0 || weeklyLimit <= 0 || weeklyAcquired > weeklyLimit)
            throw new InvalidOperationException($"Invalid live weekly count/cap: {weeklyAcquired}/{weeklyLimit}.");
        return new(playerState.ContentId, currency.ItemId, weeklyAcquired, weeklyLimit, held);
    }

    private List<DungeonOption> cachedDungeons = [];
    private List<GearsetOption> cachedGearsets = [];
    private bool dungeonDiscoveryAttempted;
    public IReadOnlyList<DungeonOption> DungeonChoices => cachedDungeons;
    public uint SelectedTerritory { get; private set; }
    public DutyModeSelection SelectedMode { get; private set; } = DutyModeSelection.Support;
    // -1 means inspect current equipment without selecting a stored gearset.
    public int SelectedGearsetId { get; private set; } = -1;
    public IReadOnlyList<GearsetOption> GearsetChoices => cachedGearsets;
    public string EligibilityFingerprint =>
        $"{playerState.ContentId}:{SelectedTerritory}:{SelectedMode}:{SelectedGearsetId}";

    public void SelectEligibility(uint territoryId, DutyModeSelection mode)
    {
        SelectedTerritory = territoryId;
        SelectedMode = mode;
    }

    public void SelectGearset(int id) => SelectedGearsetId = id;

    public IReadOnlyList<DebugTest> CreateTests() =>
    [
        new("AutoDuty launch readiness", "Inspect read-only AutoDuty mode/loop settings for the selected dungeon. Does not launch.", ReadAutoDutyReadiness),
        new("AutoDuty paths", "Query AutoDuty's read-only state and ContentHasPath for discovered dungeons. Never queues a duty.", ReadAutoDuty),
        new("Capped currency", "Resolve the current capped item and its reward column from game sheets.", ReadCurrency),
        new("Dungeon eligibility", "Check the selected dungeon and Support/Trust mode without changing game or provider state.", ReadEligibility),
        new("Dungeon rewards", "Discover positive-reward dungeons and show boss reward breakdowns and NPC-mode sheet evidence.", ReadDungeons),
        new("Live tome counts", "Read the logged-in character's held balance, weekly acquired count, and live cap.", ReadCounts),
        new("WahTools discovery", "Read-only check of Dalamud metadata, ECommons plugin-instance lookup, Feesh module, and status reflection. Does not call voyage IPC.", ReadWahToolsDiscovery),
    ];

    private DebugReport ReadWahToolsDiscovery()
    {
        var status = feeshStatusReader.Read();
        var discovery = feeshStatusReader.LastDiscovery;
        var completed = discovery.Stage == "FeeshStatus" && status.Available;
        var text = new StringBuilder()
            .AppendLine($"Observed UTC: {discovery.ObservedUtc:O}")
            .AppendLine($"Installed metadata found: {discovery.MetadataFound}")
            .AppendLine($"Installed metadata loaded: {discovery.MetadataLoaded}")
            .AppendLine($"WahTools version: {discovery.PluginVersion ?? "unknown"}")
            .AppendLine($"Plugin instance resolved: {discovery.PluginInstanceResolved?.ToString() ?? "not reached"}")
            .AppendLine($"Feesh module found: {discovery.FeeshModuleFound?.ToString() ?? "not reached"}")
            .AppendLine($"Feesh module loaded: {discovery.FeeshModuleLoaded?.ToString() ?? "not reached"}")
            .AppendLine($"Failure/read stage: {discovery.Stage}")
            .AppendLine($"Trip initialized/running: {(status.Available ? status.TripInitialized.ToString() : "unknown")}/{Show(status.TripRunning)}")
            .AppendLine($"On boat: {Show(status.OnBoat)}")
            .AppendLine($"Cleanup: Cleaning={Show(status.Cleaning)}, Cleaner={Show(status.CleanerRunning)}, Desynther={Show(status.DesyntherRunning)}, Discarder={Show(status.DiscarderRunning)}, Buyer={Show(status.BuyerBusy)}")
            .AppendLine($"Overall clean: {status.IsClean}")
            .AppendLine($"Detail: {discovery.Detail}")
            .AppendLine("No launch, stop, character-switch, or provider-control IPC was called.")
            .ToString();
        return new(completed ? "Completed" : "Unavailable", text);
    }

    private static string Show(bool? value) => value?.ToString() ?? "unknown";

    private sealed record Currency(uint ItemId, string Name, uint Category, int SheetLimit)
    {
        public string Column => Category switch
        {
            1 => "A", 2 => "B", 3 => "C",
            _ => throw new InvalidOperationException($"Unsupported currency category {Category}."),
        };
    }

    private sealed record Dungeon(string Name, uint Territory, uint Cfc, uint Instance,
        int FinalBoss, int[] Bosses, bool Support, bool Trust)
    {
        public int Total => FinalBoss + Bosses.Sum();
    }

    private Currency ResolveCurrency()
    {
        var mappings = data.GetExcelSheet<TomestonesItem>()
            .Where(row => row.Tomestones.RowId != 0 && row.Tomestones.Value.WeeklyLimit > 0)
            .ToArray();
        if (mappings.Length != 1)
            throw new InvalidOperationException($"Expected one capped mapping; found {mappings.Length}.");
        var mapping = mappings[0];
        var currency = new Currency(mapping.Item.RowId, mapping.Item.Value.Name.ToString(),
            mapping.Tomestones.RowId, mapping.Tomestones.Value.WeeklyLimit);
        _ = currency.Column;
        return currency;
    }

    private DebugReport ReadCurrency()
    {
        var currency = ResolveCurrency();
        return new("Completed", $"{CurrencyText(currency)}\n\nSource: installed game sheets.\n" +
            "The sheet limit is diagnostic; the live client limit is authoritative for planning.");
    }

    private unsafe DebugReport ReadCounts()
    {
        if (!client.IsLoggedIn || objects.LocalPlayer is not { } player)
            return new("Unavailable", "Log into a character before reading live currency values.");
        var currency = ResolveCurrency();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return new("Unavailable", "InventoryManager is not available.");
        var acquired = inventory->GetWeeklyAcquiredTomestoneCount();
        var limit = InventoryManager.GetLimitedTomestoneWeeklyLimit();
        return new("Completed", $"Character: {player.Name}\n\n{CurrencyText(currency)}\n\n" +
            $"Held balance: {inventory->GetTomestoneCount(currency.ItemId)}\n" +
            $"Weekly acquired: {acquired}\nLive weekly cap: {limit}\n" +
            $"Remaining to cap: {Math.Max(0, limit - acquired)}\n\n" +
            "Snapshot only; rerun to refresh. Spending tomes does not reduce weekly acquired.");
    }

    public unsafe DashboardSnapshot ReadDashboardSnapshot(uint selectedTerritory, DutyModeSelection mode)
    {
        var captured = DateTimeOffset.UtcNow;
        if (!dungeonDiscoveryAttempted)
        {
            DiscoverDungeons(ResolveCurrency());
            dungeonDiscoveryAttempted = true;
        }

        if (selectedTerritory == 0 && cachedDungeons.Count > 0)
            selectedTerritory = cachedDungeons[0].Territory;

        DebugReport? eligibility = null;
        if (client.IsLoggedIn)
        {
            try { eligibility = ReadEligibility(selectedTerritory, mode, -1); }
            catch (Exception error) { eligibility = new DebugReport("Unknown", error.GetBaseException().Message); }
        }

        if (!client.IsLoggedIn || objects.LocalPlayer is not { } player)
            return new(captured, false, null, string.Empty, string.Empty, null, null, null,
                ReadAutoDutyStopped(), selectedTerritory, mode, eligibility);

        var currency = ResolveCurrency();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return new(captured, true, playerState.ContentId, player.Name.ToString(), currency.Name, null, null, null,
                ReadAutoDutyStopped(), selectedTerritory, mode, eligibility,
                "InventoryManager is unavailable.", player.HomeWorld.Value.Name.ToString());

        return new(captured, true, playerState.ContentId, player.Name.ToString(), currency.Name,
            inventory->GetWeeklyAcquiredTomestoneCount(), InventoryManager.GetLimitedTomestoneWeeklyLimit(),
            inventory->GetTomestoneCount(currency.ItemId), ReadAutoDutyStopped(), selectedTerritory, mode, eligibility,
            CharacterHomeWorld: player.HomeWorld.Value.Name.ToString());
    }

    public bool? ReadAutoDutyStopped()
    {
        try { return pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped").InvokeFunc(); }
        catch { return null; }
    }

    private unsafe List<Dungeon> DiscoverDungeons(Currency currency)
    {
        var instances = data.GetExcelSheet<Lumina.Excel.Sheets.InstanceContent>();
        var dawn = data.GetExcelSheet<DawnContent>()
            .Where(row => row.Content.RowId != 0)
            .ToDictionary(row => row.Content.RowId);
        var participants = data.GetSubrowExcelSheet<DawnContentParticipable>();
        var result = new List<Dungeon>();
        foreach (var condition in data.GetExcelSheet<ContentFinderCondition>())
        {
            if (condition.ContentType.RowId != 2 || condition.ContentLinkType != 1)
                continue;
            var instance = instances.GetRow(condition.Content.RowId);
            var bosses = new int[5];
            for (var index = 0; index < bosses.Length; index++)
                bosses[index] = currency.Category switch
                {
                    1 => instance.BossCurrencyA[index],
                    2 => instance.BossCurrencyB[index],
                    3 => instance.BossCurrencyC[index],
                    _ => throw new InvalidOperationException("Unsupported currency category."),
                };
            var final = currency.Category switch
            {
                1 => instance.FinalBossCurrencyA,
                2 => instance.FinalBossCurrencyB,
                3 => instance.FinalBossCurrencyC,
                _ => throw new InvalidOperationException("Unsupported currency category."),
            };
            if (final + bosses.Sum() <= 0)
                continue;
            var hasDawn = dawn.TryGetValue(condition.RowId, out var npc);
            result.Add(new Dungeon(condition.Name.ToString(), condition.TerritoryType.RowId,
                condition.RowId, instance.RowId, final, bosses,
                hasDawn && participants.GetSubrowCount(npc.RowId) > 1,
                hasDawn && npc.Unknown13));
        }
        var ordered = result.OrderBy(row => row.Territory).ToList();
        var conditions = data.GetExcelSheet<ContentFinderCondition>();
        cachedDungeons = ordered.Select(row =>
        {
            var condition = conditions.GetRow(row.Cfc);
            return new DungeonOption(row.Territory, row.Cfc, row.Instance, row.Name, row.Total,
                condition.ClassJobLevelRequired, condition.ItemLevelRequired, row.Support, row.Trust);
        }).ToList();
        RefreshGearsets();
        return ordered;
    }

    private unsafe void RefreshGearsets()
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            cachedGearsets = [];
            return;
        }

        var jobs = data.GetExcelSheet<ClassJob>();
        var gearsets = new List<GearsetOption>();
        for (var id = 0; id < 100; id++)
        {
            if (!module->IsValidGearset(id))
                continue;
            var entry = module->GetGearset(id);
            if (entry == null)
                continue;
            var job = jobs.GetRow(entry->ClassJob);
            gearsets.Add(new GearsetOption(id, entry->Name.ToString(), entry->ClassJob,
                job.Name.ToString(), job.CanQueueForDuty, entry->ItemLevel));
        }
        cachedGearsets = gearsets;
    }

    private DebugReport ReadDungeons()
    {
        var currency = ResolveCurrency();
        var dungeons = DiscoverDungeons(currency);
        if (SelectedTerritory == 0 && cachedDungeons.Count > 0)
            SelectedTerritory = cachedDungeons[0].Territory;
        var text = new StringBuilder(CurrencyText(currency));
        text.AppendLine($"\n\nDiscovered {dungeons.Count} positive-reward dungeons.");
        text.AppendLine("Normal full-clear rewards only; no roulette/new-player bonuses.");
        foreach (var dungeon in dungeons)
        {
            text.AppendLine($"\n{dungeon.Name}: {dungeon.Total} tomes");
            text.AppendLine($"  Territory {dungeon.Territory} | CFC {dungeon.Cfc} | Instance {dungeon.Instance}");
            text.AppendLine($"  Bosses [{string.Join(", ", dungeon.Bosses)}] + final {dungeon.FinalBoss}");
            text.AppendLine($"  Support evidence: {dungeon.Support} | Trust evidence: {dungeon.Trust}");
        }
        text.AppendLine("\nNPC-mode evidence is patch-sensitive. Unlocks, job/item level, Trust levels,");
        text.AppendLine("and installed AutoDuty eligibility are not verified by this test.");
        return new("Completed", text.ToString());
    }

    private DebugReport ReadAutoDuty()
    {
        bool stopped;
        try
        {
            stopped = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped").InvokeFunc();
        }
        catch (Exception error)
        {
            return new("Unavailable", $"AutoDuty.IsStopped could not be read.\n{error.GetType().Name}: {error.Message}\n\n" +
                "Install/enable a compatible AutoDuty version. No provider actions were requested.");
        }
        var paths = pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
        var text = new StringBuilder($"AutoDuty IsStopped: {stopped}\n\n");
        foreach (var dungeon in DiscoverDungeons(ResolveCurrency()))
            text.AppendLine($"{dungeon.Name} ({dungeon.Territory}): path = {paths.InvokeFunc(dungeon.Territory)}");
        text.AppendLine("\nPath availability does not guarantee Support/Trust eligibility.");
        text.AppendLine("No configuration, queue, start, or stop calls were made.");
        return new("Completed", text.ToString());
    }

    private DebugReport ReadAutoDutyReadiness()
    {
        var currency = ResolveCurrency();
        DiscoverDungeons(currency);
        var dungeon = cachedDungeons.FirstOrDefault(item => item.Territory == SelectedTerritory);
        if (dungeon is null)
            return new("Unavailable", "Run Dungeon rewards first, then select a discovered dungeon.");

        var getConfig = pluginInterface.GetIpcSubscriber<string, string>("AutoDuty.GetConfig");
        var config = new[]
        {
            ("Meta.AutoDutyModeEnum", "AutoDuty execution mode"),
            ("Meta.DutyModeEnum", "Configured duty mode"),
            ("Meta.LoopTimes", "Configured loop count"),
            ("DutyConfig.AutoExitDuty", "Auto-exit duty"),
            ("Loop.Pre.Enabled", "Pre-loop actions enabled"),
            ("Loop.Between.Enabled", "Between-loop actions enabled"),
            ("Loop.Termination.Enabled", "Termination actions enabled"),
        };
        var text = new StringBuilder($"Selected dungeon: {dungeon.Name} (Territory {dungeon.Territory})\n" +
            $"Selected TT mode: {SelectedMode}\n\n");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, label) in config)
        {
            try
            {
                var value = getConfig.InvokeFunc(key);
                values[key] = value;
                text.AppendLine($"{label}: {(string.IsNullOrWhiteSpace(value) ? "<unavailable>" : value)}");
            }
            catch (Exception error)
            {
                values[key] = string.Empty;
                text.AppendLine($"{label}: <unavailable: {error.GetBaseException().Message}>");
            }
        }

        bool? stopped = null;
        try
        {
            stopped = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped").InvokeFunc();
            text.AppendLine($"AutoDuty stopped: {stopped}");
        }
        catch (Exception error)
        {
            stopped = null;
            text.AppendLine($"AutoDuty stopped: <unavailable: {error.GetBaseException().Message}>");
        }

        var modeValue = values.GetValueOrDefault("Meta.DutyModeEnum");
        var loopMode = string.Equals(values.GetValueOrDefault("Meta.AutoDutyModeEnum"), "Looping", StringComparison.OrdinalIgnoreCase);
        var singleLoop = values.GetValueOrDefault("Meta.LoopTimes") == "1";
        var modeMatches = string.Equals(modeValue, SelectedMode.ToString(), StringComparison.OrdinalIgnoreCase);
        text.AppendLine($"\nConfigured mode matches selection: {modeMatches}");
        text.AppendLine($"Looping mode already selected: {loopMode}");
        text.AppendLine($"Loop count already one: {singleLoop}");
        text.AppendLine($"Provider idle: {stopped?.ToString() ?? "unknown"}");

        bool? selectedPath = null;
        try
        {
            var path = pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath")
                .InvokeFunc(dungeon.Territory);
            selectedPath = path;
            text.AppendLine($"Selected dungeon path available: {path}");
        }
        catch (Exception error)
        {
            text.AppendLine($"Selected dungeon path: <unavailable: {error.GetBaseException().Message}>");
        }

        text.AppendLine("\nAutoDuty.Run forces AutoDutyModeEnum to Looping. TT requires Looping/one loop already configured and passes loops=0, so it does not request a loop-count write.");
        text.AppendLine("bareMode temporarily disables normal pre-, between-, and termination-action groups; AutoDuty restores their enabled flags in StopAndResetAll. Locked provider actions may still be evaluated.");
        text.AppendLine("AutoDuty's normal stop globally calls PopConfigOverrides without an owner token. TT never pushes or pops overrides.");
        text.AppendLine("A TT one-run action requires the user to keep other integrations from controlling AutoDuty or pushing overrides until the run is released.");
        text.AppendLine($"TT Enable AutoDuty loop actions: {configuration.EnableAutoDutyLoopActions}; proposed bareMode: {SingleRunPolicy.BareModeFor(configuration.EnableAutoDutyLoopActions)}.");
        text.AppendLine("No AutoDuty settings or provider actions were changed by this diagnostic.");

        var valuesKnown = config.All(entry => values.TryGetValue(entry.Item1, out var value) && !string.IsNullOrWhiteSpace(value));
        var status = stopped == false || selectedPath == false ? "Blocked" : stopped is null || selectedPath is null || !valuesKnown ? "Unknown" :
            modeMatches && loopMode && singleLoop ? "Provider settings compatible; explicit ownership confirmation required" : "Blocked";
        return new(status, text.ToString());
    }

    private unsafe DebugReport ReadEligibility()
        => ReadEligibility(SelectedTerritory, SelectedMode, SelectedGearsetId);

    private unsafe DebugReport ReadEligibility(uint territoryId, DutyModeSelection mode, int gearsetId)
    {
        if (!client.IsLoggedIn || objects.LocalPlayer is not { } localPlayer)
            return new("Unavailable", "Log into a character before checking dungeon eligibility.");

        var currency = ResolveCurrency();
        DiscoverDungeons(currency);
        var dungeon = cachedDungeons.FirstOrDefault(item => item.Territory == territoryId);
        if (dungeon is null)
            return new("Unavailable", "Run Dungeon rewards first, then select a discovered dungeon.");

        var checks = new List<EligibilityCheck>();
        Add(checks, "Logged in", EligibilityStatus.Eligible,
            $"{playerState.CharacterName} (Content ID {playerState.ContentId})", "Logged-in character",
            "Local player is available.", "Dalamud client/player state");

        var instance = data.GetExcelSheet<Lumina.Excel.Sheets.InstanceContent>().GetRow(dungeon.Instance);
        bool unlocked;
        try
        {
            unlocked = unlocks.IsInstanceContentUnlocked(instance);
            Add(checks, "Dungeon unlocked", unlocked ? EligibilityStatus.Eligible : EligibilityStatus.Blocked,
                unlocked.ToString(), "true", unlocked ? "Current character has unlocked this instance." :
                "Unlock the dungeon on this character before running it.", "Dalamud IUnlockState / InstanceContent");
        }
        catch (Exception error)
        {
            Add(checks, "Dungeon unlocked", EligibilityStatus.Unknown, "unavailable", "true",
                error.Message, "Dalamud IUnlockState");
        }

        var classJob = playerState.ClassJob.ValueNullable;
        var level = playerState.Level;
        var canQueueForDuty = classJob?.CanQueueForDuty;
        Add(checks, "Combat job", canQueueForDuty is null ? EligibilityStatus.Unknown :
                canQueueForDuty.Value ? EligibilityStatus.Eligible : EligibilityStatus.Blocked,
            classJob is null ? "unavailable" : $"{classJob.Value.Name} (CanQueueForDuty={canQueueForDuty})",
            "CanQueueForDuty = true", canQueueForDuty is null ?
                "Class/job sheet row is unavailable." : canQueueForDuty.Value ?
                    "Game data identifies this as a duty-eligible combat class/job." :
                    "Current class/job cannot queue for duties; change to a combat job.",
            "Lumina ClassJob.CanQueueForDuty");
        Add(checks, "Job level", level >= dungeon.RequiredLevel ? EligibilityStatus.Eligible : EligibilityStatus.Blocked,
            classJob is null ? $"Unknown job, level {level}" : $"{classJob.Value.Name}, level {level}",
            $"Level {dungeon.RequiredLevel}+", level >= dungeon.RequiredLevel ?
                "Current job meets the sheet level requirement." : "Switch to an eligible job and meet the level requirement.",
            "Dalamud IPlayerState / ContentFinderCondition");

        ushort currentItemLevel;
        try
        {
            var uiState = UIState.Instance();
            if (uiState == null)
                throw new InvalidOperationException("Client UIState is unavailable.");
            currentItemLevel = uiState->CurrentItemLevel;
            Add(checks, "Current equipment item level",
                currentItemLevel >= dungeon.RequiredItemLevel ? EligibilityStatus.Eligible : EligibilityStatus.Blocked,
                currentItemLevel.ToString(), $"{dungeon.RequiredItemLevel}+",
                currentItemLevel >= dungeon.RequiredItemLevel ? "Current equipment meets the minimum item-level requirement." :
                    "Equip higher item-level gear before queueing.", "FFXIVClientStructs UIState.CurrentItemLevel");
        }
        catch (Exception error)
        {
            Add(checks, "Current equipment item level", EligibilityStatus.Unknown, "unavailable",
                $"{dungeon.RequiredItemLevel}+", error.Message, "FFXIVClientStructs UIState");
        }

        var modeAvailable = mode == DutyModeSelection.Support ? dungeon.Support : dungeon.Trust;
        Add(checks, mode + " availability",
            modeAvailable ? EligibilityStatus.Eligible : EligibilityStatus.Blocked,
            modeAvailable.ToString(), "true", modeAvailable ?
                "Game sheets indicate this NPC mode is supported." :
                $"This dungeon has no {mode} support in the current sheets.", "DawnContent sheets");
        if (gearsetId < 0)
        {
            Add(checks, "Gearset selection", EligibilityStatus.Eligible, "Current equipment",
                "Current equipment selected", "No stored gearset was requested; current equipment checks apply.",
                "User selection");
        }
        else
        {
            var gearset = cachedGearsets.FirstOrDefault(set => set.Id == gearsetId);
            if (gearset is null)
                Add(checks, "Selected gearset", EligibilityStatus.Blocked, gearsetId.ToString(),
                    "Valid stored gearset", "Selected gearset is no longer available; refresh Dungeon rewards.",
                    "FFXIVClientStructs RaptureGearsetModule");
            else
            {
                Add(checks, "Selected gearset", gearset.CombatJob ? EligibilityStatus.Eligible : EligibilityStatus.Blocked,
                    $"{gearset.Name} — {gearset.JobName}, iLvl {gearset.ItemLevel}", "Valid combat gearset",
                    gearset.CombatJob ? "Stored gearset belongs to a duty-eligible combat job." :
                        "Stored gearset is not a duty-eligible combat job.", "RaptureGearsetModule / ClassJob.CanQueueForDuty");
                var gearsetItemLevelStatus = gearset.ItemLevel >= dungeon.RequiredItemLevel
                    ? EligibilityStatus.Eligible : EligibilityStatus.Blocked;
                var selectedJobIsCurrent = classJob?.RowId == gearset.ClassJob;
                var gearsetLevelStatus = !selectedJobIsCurrent ? EligibilityStatus.Unknown :
                    playerState.Level >= dungeon.RequiredLevel ? EligibilityStatus.Eligible : EligibilityStatus.Blocked;
                var gearsetRequirementsStatus = !gearset.CombatJob || gearsetItemLevelStatus == EligibilityStatus.Blocked ||
                                               gearsetLevelStatus == EligibilityStatus.Blocked
                    ? EligibilityStatus.Blocked
                    : gearsetLevelStatus == EligibilityStatus.Unknown ? EligibilityStatus.Unknown : EligibilityStatus.Eligible;
                Add(checks, "Selected gearset meets requirements", gearsetRequirementsStatus,
                    $"{gearset.JobName}, gearset iLvl {gearset.ItemLevel}; " +
                    (selectedJobIsCurrent ? $"current job level {playerState.Level}" : "gearset job level unavailable"),
                    $"Job level {dungeon.RequiredLevel}+ and gearset iLvl {dungeon.RequiredItemLevel}+",
                    !selectedJobIsCurrent ? "Stored gearset job differs from current job; that job's level is not verified. This test does not equip it." :
                    gearsetRequirementsStatus == EligibilityStatus.Eligible ? "Stored job level and gearset item level meet sheet minimums." :
                    "Stored gearset item level or current matching job level is below the sheet minimum.",
                    "RaptureGearsetModule / IPlayerState / ContentFinderCondition");
                var module = RaptureGearsetModule.Instance();
                var isEquipped = module != null && module->CurrentGearsetIndex == gearset.Id;
                Add(checks, "Selected gearset equipped",
                    isEquipped ? EligibilityStatus.Eligible : EligibilityStatus.Unknown,
                    isEquipped ? "yes" : "no", "equipped before queue",
                    isEquipped ? "Selected set is currently equipped." :
                        "This read-only test will not equip the selected set; current equipment iLvl check is separate.",
                    "RaptureGearsetModule.CurrentGearsetIndex");
            }
        }

        try
        {
            var hasPath = pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath")
                .InvokeFunc(dungeon.Territory);
            Add(checks, "AutoDuty path", hasPath ? EligibilityStatus.Eligible : EligibilityStatus.Blocked,
                hasPath.ToString(), "true", hasPath ? "Installed AutoDuty reports a path." :
                    "No installed AutoDuty path for this territory.", "AutoDuty.ContentHasPath(TerritoryType)");
        }
        catch (Exception error)
        {
            Add(checks, "AutoDuty path", EligibilityStatus.Unknown, "unavailable", "true",
                error.Message, "AutoDuty IPC");
        }

        try
        {
            var autoDutyStopped = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped").InvokeFunc();
            Add(checks, "AutoDuty idle", autoDutyStopped ? EligibilityStatus.Eligible : EligibilityStatus.Blocked,
                autoDutyStopped.ToString(), "true", autoDutyStopped ? "AutoDuty reports stopped." :
                "AutoDuty is active; wait for its operation to finish.", "AutoDuty.IsStopped");
        }
        catch (Exception error)
        {
            Add(checks, "AutoDuty idle", EligibilityStatus.Unknown, "unavailable", "true",
                error.Message, "AutoDuty IPC");
        }

        var inDuty = conditions[ConditionFlag.BoundByDuty] || conditions[ConditionFlag.BoundByDuty56] ||
                     conditions[ConditionFlag.BoundByDuty95];
        Add(checks, "Active duty", inDuty
                ? EligibilityStatus.Blocked : EligibilityStatus.Eligible,
            inDuty.ToString(), "not in duty",
            inDuty ? "Exit the active duty first." : "Not bound by duty.",
            "Dalamud BoundByDuty / BoundByDuty56 / BoundByDuty95");
        Add(checks, "Combat", conditions[ConditionFlag.InCombat]
                ? EligibilityStatus.Blocked : EligibilityStatus.Eligible,
            conditions[ConditionFlag.InCombat].ToString(), "not in combat",
            conditions[ConditionFlag.InCombat] ? "Wait until combat ends." : "Not in combat.",
            "Dalamud ConditionFlag.InCombat");

        Add(checks, "Duty queue", conditions[ConditionFlag.WaitingForDutyFinder]
                ? EligibilityStatus.Blocked : EligibilityStatus.Eligible,
            conditions[ConditionFlag.WaitingForDutyFinder].ToString(), "not queued",
            conditions[ConditionFlag.WaitingForDutyFinder] ? "Cancel or complete the existing duty queue first." :
                "Not waiting in Duty Finder.", "Dalamud ConditionFlag.WaitingForDutyFinder");
        var changingArea = conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51];
        Add(checks, "Area transition", changingArea ? EligibilityStatus.Blocked : EligibilityStatus.Eligible,
            changingArea.ToString(), "not transitioning", changingArea ? "Wait for the area transition to finish." :
                "No area transition detected.", "Dalamud BetweenAreas / BetweenAreas51");
        Add(checks, "Occupied state", conditions[ConditionFlag.Occupied]
                ? EligibilityStatus.Blocked : EligibilityStatus.Eligible,
            conditions[ConditionFlag.Occupied].ToString(), "not occupied",
            conditions[ConditionFlag.Occupied] ? "Character is occupied; wait for the current interaction/action to finish." :
                "No generic occupied state detected.", "Dalamud ConditionFlag.Occupied");
        try
        {
            var retainerBusy = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy")
                .InvokeFunc();
            var retainerMultiMode = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.GetMultiModeStatus")
                .InvokeFunc();
            var retainerIdle = !retainerBusy && !retainerMultiMode;
            Add(checks, "AutoRetainer idle", retainerIdle ? EligibilityStatus.Eligible : EligibilityStatus.Blocked,
                $"busy={retainerBusy}, multiMode={retainerMultiMode}", "not busy and Multi Mode disabled",
                retainerBusy ? "AutoRetainer is busy; wait for its operation to finish." : retainerMultiMode ?
                    "AutoRetainer Multi Mode is active; disable it before a future dungeon run." :
                    "AutoRetainer is idle.", "AutoRetainer.PluginState.IsBusy / GetMultiModeStatus");
        }
        catch (Exception error)
        {
            Add(checks, "AutoRetainer idle", EligibilityStatus.Unknown, "busy/Multi Mode state unavailable",
                "not busy and Multi Mode disabled", error.Message, "AutoRetainer IPC");
        }

        int? acquiredValue = null;
        int? weeklyLimitValue = null;
        int? heldValue = null;
        unsafe
        {
            var inventory = InventoryManager.Instance();
            if (inventory != null)
            {
                acquiredValue = inventory->GetWeeklyAcquiredTomestoneCount();
                weeklyLimitValue = InventoryManager.GetLimitedTomestoneWeeklyLimit();
                heldValue = checked((int)inventory->GetTomestoneCount(currency.ItemId));
            }
        }
        var readiness = TomeRewardPolicy.Evaluate(dungeon.Reward, acquiredValue, weeklyLimitValue,
            heldValue, TomestoneHoldingLimit);
        Add(checks, "Weekly tome headroom", readiness.WeeklyStatus,
            acquiredValue is null || weeklyLimitValue is null ? "unavailable" : $"{acquiredValue}/{weeklyLimitValue} acquired",
            "weekly remaining > 0",
            readiness.WeeklyRemaining is null ? readiness.Explanation :
                $"Weekly remaining: {readiness.WeeklyRemaining}; expected cap-clamped payout: {readiness.ExpectedPayout}.",
            "InventoryManager weekly count/live limit");
        Add(checks, "Held tome storage headroom", readiness.StorageStatus,
            heldValue is null ? "unavailable" : $"{heldValue}/{TomestoneHoldingLimit} held",
            "storage room >= expected payout",
            readiness.StorageRoom is null ? readiness.Explanation :
                $"Storage room: {readiness.StorageRoom}; expected cap-clamped payout: {readiness.ExpectedPayout}. {readiness.Explanation}",
            "InventoryManager held count / FFXIV tomestone holding rule (2,000 each)");

        var report = new EligibilityReport(checks);
        var text = new StringBuilder($"Character: {localPlayer.Name}\nDungeon: {dungeon.Name}\nMode: {mode}\n" +
            $"Expected reward: {dungeon.Reward} {currency.Name}\nCaptured UTC: {DateTimeOffset.UtcNow:O}\n\n");
        var characterCheckNames = new HashSet<string>
        {
            "Logged in", "Dungeon unlocked", "Combat job", "Job level", "Current equipment item level",
            "Support availability", "Trust availability", "Trust party levels/roles", "Gearset selection",
            "Selected gearset", "Selected gearset meets requirements", "Selected gearset equipped",
        };
        var launchCheckNames = new HashSet<string>
        {
            "AutoDuty path", "AutoDuty idle", "Active duty", "Combat", "Duty queue", "Area transition",
            "Occupied state", "AutoRetainer idle",
        };
        var characterChecks = checks.Where(check => characterCheckNames.Contains(check.Name)).ToArray();
        var launchChecks = checks.Where(check => launchCheckNames.Contains(check.Name)).ToArray();
        var rewardChecks = checks.Where(check => check.Name is "Weekly tome headroom" or "Held tome storage headroom").ToArray();
        AppendSection(text, "CHARACTER / DUNGEON ELIGIBILITY", characterChecks);
        AppendSection(text, "RUNTIME CHECKS", launchChecks);
        AppendSection(text, "TOME REWARD-TEST READINESS", rewardChecks);
        text.AppendLine("\nTrust member selection/levels are managed by AutoDuty; TT does not inspect or change them.");
        text.AppendLine("Combat role uses ClassJob.CanQueueForDuty; this confirms queueable job type, not duty-specific party composition.");
        text.AppendLine("\nRuntime checks are not a complete queue-acceptance guarantee; no queue was requested.");
        text.AppendLine($"\nCHARACTER/DUNGEON: {new EligibilityReport(characterChecks).Overall}");
        text.AppendLine($"RUNTIME CHECKS: {new EligibilityReport(launchChecks).Overall}");
        text.AppendLine($"TOME TEST: {new EligibilityReport(rewardChecks).Overall}");
        text.AppendLine($"\nALL CHECKS: {report.Overall}");
        text.AppendLine("Read-only check. No gearset, queue, or provider configuration was changed.");
        return new(report.Overall.ToString(), text.ToString());
    }

    public DebugReport CheckEligibility(uint territoryId, DutyModeSelection mode, int gearsetId)
    {
        return ReadEligibility(territoryId, mode, gearsetId);
    }

    private static void Add(List<EligibilityCheck> checks, string name, EligibilityStatus status,
        string observed, string required, string explanation, string source) =>
        checks.Add(new(name, status, observed, required, explanation, source));

    private static void AppendSection(StringBuilder text, string title, IReadOnlyList<EligibilityCheck> checks)
    {
        text.AppendLine($"\n{title}");
        text.AppendLine($"Section status: {new EligibilityReport(checks).Overall}");
        foreach (var check in checks)
            text.AppendLine($"\n{check.Name}: {check.Status}\n  Observed: {check.Observed}\n  Required: {check.Required}\n" +
                $"  Detail: {check.Explanation}\n  Source: {check.Source}");
    }

    private static string CurrencyText(Currency currency) =>
        $"Currency: {currency.Name}\nItem ID: {currency.ItemId}\n" +
        $"Tomestones category: {currency.Category}\nReward column: Currency {currency.Column}\n" +
        $"Sheet weekly limit: {currency.SheetLimit}";
}
