# Tomes & Tides

## Scope and execution

- Current implementation has themed tabbed main/diagnostics windows, explicitly confirmed single-run action, persisted weekly dungeon farming across 1–7 UTC periods, read-only AutoRetainer readiness, and optional Retainers-only handoffs linked to the confirmed farming plan. Ocean Fishing remains out of scope. Read-only diagnostic tests never start/stop providers or mutate configuration.
- `TomesAndTides/Plugin.cs` wires services, commands, UI, and framework updates. `/tomestides` is primary; register `/tt` only if available and remove it only if registration succeeded.
- `Debugging/DebugSession.cs` queues explicit user requests and runs at most one test per framework update. Keep native game and IPC reads in this flow, not in window rendering or background tasks. Eligibility output distinguishes blocked from unknown and is split into character/dungeon, launch-now, and tome-test readiness.
- `Debugging/FarmingSchedulePolicy.cs` owns pure reset/period/allocation math; `FarmingPlanController.cs` owns persisted weekly plan progression on framework updates. Plan runs link persisted plan/period IDs to `DungeonRunController`; credit only a verified run, persist credit before releasing the run record, and never replay an ambiguous launch. Stop cancels pending dispatch and allows active operation to settle. Reset/deadline forbid new dispatch.
- `Debugging/RetainerHandoffController.cs` owns retainer checkpoints linked to verified farming operations, at-most-once AutoRetainer pass, stable completion/idle observation, one-time Lifestream return, Stop cleanup, and reload/ambiguous recovery. Service permission and near-complete wait (0–30 minutes) are captured in the farming plan. `Integrations/AutoRetainerClient.cs` and `LifestreamClient.cs` are the IPC boundaries; all provider calls remain on framework updates.
- `Debugging/DebugTests.cs` owns live reads; `Windows/MainWindow.cs` renders cached dashboard snapshots and owns launch selections, while `Windows/DebugWindow.cs` owns independent diagnostic selections and separate test/results child panes. Results are retained snapshots, not continuous monitoring; unavailable/failed reads must not become successful zero counts. `Windows/TomesTheme.cs` scopes standard-ImGui styling to TT windows and restores it afterward.
- The explicit one-run controller may call `AutoDuty.Run` only after fresh framework-thread checks; it must require AutoDuty already stopped, Looping, one configured loop, and matching duty mode. Use `loops=0` so TT does not overwrite the configured loop count. AutoDuty's Run IPC sets the already-required Looping mode and its stop path globally pops overrides without owner tokens. TT never calls SetConfig/PushConfigOverrides/PopConfigOverrides; the confirmation must disclose that the user must keep other integrations from controlling AutoDuty or pushing overrides until TT releases its one-run ownership. Never stop/abandon an uncertain run or retry. Persist launch intent before dispatch; reload/ambiguous outcomes require acknowledgment. bareMode defaults true; its option is captured at confirmation. No calls from diagnostic tests or ImGui drawing: all IPC/native reads and provider calls run on framework updates.
- Use Dalamud `IPluginLog` for diagnostics and one-run lifecycle transitions; include the persisted run operation ID on run messages, log state transitions rather than polling, and redact character Content IDs from report logs.

## Build and verification

Run commands from this directory; no solution file exists.

- Requires .NET 10, Windows/x64, and Dalamud API 15 development files (`Dalamud.NET.Sdk/15.0.0`).
- Build: `dotnet build TomesAndTides/TomesAndTides.csproj -c Release --arch x64` (includes restore).
- In this WSL environment, `dotnet` is absent; invoke `"/mnt/c/Program Files/dotnet/dotnet.exe"` instead. Installed Dalamud files are at `C:\Users\baand\AppData\Roaming\XIVLauncher\addon\Hooks\dev`.
- Development DLL: `D:\Coding\C#\Tomes & Tides\TomesAndTides\bin\x64\Release\TomesAndTides.dll`. Preserve the explicit output-path settings and `TomesAndTides/packages.lock.json`.
- Tests: `python3 -B -m unittest discover -s tests -v`. Focused policy suite: `python3 -B -m unittest discover -s tests -v -k RewardPolicyTests`. These test the Python audit, not the live C# plugin; there is no `dotnet test` project.
- C# eligibility policy assertions: `dotnet run --project tests/TomesAndTides.PolicyTests.csproj`; plain console app linking the pure policy model, not a plugin/integration test.
- Audit: `python3 -B tools/audit_dungeon_rewards.py`; prints JSON without modifying references. Defaults require adjacent `../ffxiv-knowledge`; override `--sheets` and `--autoduty-paths` for other checkouts.
- `LocalSnapshotTests` skips without the reference CSVs and intentionally asserts the Mnemonics-era snapshot when present. Updated reference data may require updating the fixture, not hardcoding production behavior to satisfy it.
- No configured lint, formatter, codegen, or CI workflow. Build/offline tests do not validate ImGui layout, native reads, or installed provider compatibility; those require in-game checks.

## Game-data contracts

- Read `docs/dungeon-rewards.md` for verified joins and snapshot provenance; the runtime source of truth is installed Lumina sheets, not reference CSVs or historical reward tables.
- Resolve the unique capped `TomestonesItem` mapping via `Tomestones.WeeklyLimit > 0`; categories 1/2/3 map to reward columns A/B/C. Item ID, mapping row ID, category, and inventory slot are different identifiers. Fail closed on ambiguous/unsupported mappings.
- Repeatable reward is `FinalBossCurrencyX + sum(BossCurrencyX[0..4])`; exclude new-player and roulette bonuses. Mnemonics item 49/category 3 and cap 900 are snapshot facts, not constants.
- Live weekly progress comes from `InventoryManager.GetWeeklyAcquiredTomestoneCount()`, not held balance; use `GetLimitedTomestoneWeeklyLimit()` for the live cap.
- Held balance comes from `GetTomestoneCount(itemId)`. No numeric holding-limit field was found in inspected sheets/client APIs; current 2,000-per-tomestone rule is an explicitly documented constant in `DebugTests.cs`, separate from the dynamic weekly limit. Revalidate if the game rule changes.
- Keep TerritoryType, ContentFinderCondition, and InstanceContent IDs distinct. AutoDuty `ContentHasPath` takes TerritoryType. Path presence and DawnContent Support/Trust evidence do not establish character unlocks or runnable party eligibility; `Unknown13` is patch-sensitive.
- Adjacent AutoDuty, YAF, QR, Henchman, and `ffxiv-knowledge` trees are references, not modification targets. ECommons is a direct dependency for Lifestream's documented `ErrorCode` IPC contract; AutoRetainer object IPC payloads are decoded to local typed DTOs matching its public API fields.
