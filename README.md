# Tomes & Tides

Dalamud plugin coordinating weekly-capped tome farming and optional AutoRetainer handoffs; Ocean Fishing is planned.
Commands: `/tomestides`, alias `/tt` (subject to availability).

## Current stage

Dalamud API 15 plugin with themed tabbed main and Diagnostics windows, offline
reward audit, explicitly confirmed one-run action, and manually started persisted
weekly tome farming plan. Plan distributes dungeon runs across 1–7 reset-aligned
periods and uses AutoDuty one run at a time. Optional AutoRetainer service is
captured in the confirmed farming plan: after each verified dungeon, TT checks
ventures and may complete one Retainers-only pass and return to the farming
character before the next dungeon. The Retainers tab configures a 0–30 minute
near-complete wait (default 5). Ocean Fishing is not implemented.

## Build and load the debug plugin

Requires .NET 10 SDK and Dalamud API 15 development files.

```powershell
dotnet build TomesAndTides/TomesAndTides.csproj -c Release --arch x64
```

Add this DLL to Dalamud's development plugin locations:

```text
D:\Coding\C#\Tomes & Tides\TomesAndTides\bin\x64\Release\TomesAndTides.dll
```

Open the main window with `/tomestides` or `/tt`. If another plugin owns `/tt`,
the long command remains available. The plugin installer main/config buttons also
open the main window; the title-bar bug icon opens Diagnostics.

The themed main window uses Current Character, Farming Plan, Dungeon Setup, and
Retainers tabs; Diagnostics remains in the title-bar bug icon. Current Character
shows cached character/tome state and farming start/stop; the Farming Plan tab
shows allocations and current-period progress. Configure finish within the first
1–7 weekly periods. Periods begin at 08:00 UTC. The plan persists allocations,
period progress, stop requests, and verified operation IDs; missed work is
redistributed across remaining periods using fresh weekly progress. Stop blocks
future launches and lets an active run finish safely. A stopped plan can resume
within its original week without losing completed-period progress. Weekly reset
or selected finish deadline expires the plan; TT does not start a new week.
An active plan pauses on plugin reload and requires explicit recovery/resume or
stop; uncertain launches cannot be resumed automatically.
A shared slate/cyan ImGui theme is scoped to TT windows and popups; it uses
standard Dalamud fonts/scaling and no added UI library.

The Diagnostics window's left child pane selects tests and offers Run selected / Run all / Clear.
The separate right child pane retains each test's timestamped snapshot, errors,
and a Copy result button. The divider is resizable; results scroll independently.
Diagnostic tests execute on framework updates and only read data:

- Capped currency: installed sheet mapping and sheet weekly limit.
- Live tome counts: current logged-in character's held/weekly/live-cap values.
- Dungeon rewards: dynamic full-clear totals and Support/Trust sheet evidence.
- AutoDuty paths: read-only IPC availability/state/path queries; AutoDuty is optional.
- AutoDuty launch readiness: reads current mode, duty mode, loop count, loop-action toggles, idle state, and selected path. It never launches or changes settings.
- Dungeon eligibility: select a discovered dungeon and Support/Trust mode; reports
  unlock, `ClassJob.CanQueueForDuty`, job level, current item level, NPC-mode
  sheet evidence, path, active duty/combat/queue/transition/occupied state,
  AutoDuty idle state, AutoRetainer Multi Mode, and weekly headroom. Stored
  gearsets are selectable for read-only inspection and are never equipped.

Live values are not continuously refreshed; rerun the test after gaining tomes.
Native game reads require in-game validation. Unknown schema/provider failures
are shown in the result pane and logged, not treated as zero-valued success.
The read-only eligibility report is preliminary: AutoDuty owns Trust member
selection/levels; other automation conflicts and actual queue acceptance are
not established. Results are split into character/dungeon, runtime checks, and
tome-test readiness; changing dungeon/mode/gearset/character marks a result stale.
Held storage uses the documented 2,000-per-tomestone game rule, separate from
the dynamically read weekly cap.
The separate **Run one dungeon** action requires confirmation and a fresh
framework-thread eligibility/provider check. AutoDuty must already be stopped,
Looping, configured for exactly one loop, and set to the selected Support/Trust
mode. TT passes `loops=0` to avoid overwriting the configured count; AutoDuty's
IPC still sets its already-required Looping mode. The persisted loop-action
option defaults off: off requests `bareMode=true`; on requests `false` and warns
that configured termination actions may execute. The option is captured when
the confirmation is opened.

AutoDuty's normal stop globally calls `PopConfigOverrides` without ownership
tokens. TT never calls its Set/Push/Pop config IPC. The user must keep other
integrations from controlling AutoDuty or pushing overrides from confirmation
until the one-run record is safely acknowledged. AutoDuty provides no lock to
enforce this; that exclusivity is an explicit user responsibility. TT persists
launch intent before its single Run call, never retries ambiguous outcomes,
never issues Stop/leave commands, and requires recovery acknowledgment after a
reload. Duty completion, territory exit, stopped state, and weekly tomestone
change are observed; reward attribution is marked uncertain across weekly reset
or when the count does not increase.

Lifecycle and diagnostic messages use Dalamud `IPluginLog` and are visible in
`/xllog`. Run and plan transitions include persisted operation/plan IDs; full
diagnostic reports are logged at Debug level, without routine polling.

The farming coordinator has not yet completed in-game period-boundary, stop,
reload, deadline, and persistence-failure acceptance. An exact expected tome
delta still cannot prove exclusive attribution if other activities award the
same capped currency concurrently. Plan operation blocks automatically on
unverified reward, ambiguous launch, character change, or unknown provider/data
state. TT never retries an uncertain AutoDuty launch, stops AutoDuty, or abandons
a duty.

Retainer service permission is captured when the farming plan is confirmed and
does not start independently. Each verified dungeon creates a persisted checkpoint;
TT waits for near-complete ventures within the configured threshold, requests at
most one Retainers-only pass, verifies completion and stable provider idle, then
returns to the farming character before continuing. Uncertain pass/return outcomes
require explicit recovery. Combined-flow checkpoint, Stop, reload, and return
behavior have not yet completed in-game acceptance.

See [dungeon reward findings](docs/dungeon-rewards.md).

### Reproduce the read-only audit

From this directory, with Python 3:

```sh
python3 -B tools/audit_dungeon_rewards.py
python3 -B -m unittest discover -s tests -v
```

The audit prints JSON and makes no changes to the reference repositories.
Defaults use the adjacent `ffxiv-knowledge` checkout. Override with
`--sheets <csv/en directory>` and `--autoduty-paths <Paths directory>`.

Policy tests use synthetic data. The optional local-snapshot regression test
checks the supplied Mnemonics rewards against the adjacent CSV checkout; it
intentionally needs updating when the reference data moves to another tome era.

Eligibility and farming-schedule policy checks (plain .NET console executable):

```sh
dotnet run --project tests/TomesAndTides.PolicyTests.csproj
```
