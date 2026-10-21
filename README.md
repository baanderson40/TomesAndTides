# Tomes & Tides

Planned Dalamud scheduler for weekly-capped tomestones, Ocean Fishing, and AutoRetainer.
Commands: `/tomestides`, alias `/tt` (subject to availability).

## Current stage

Minimal Dalamud API 15 plugin with a themed main window, a separate Diagnostics
window, and the Phase 1 offline reward audit. Dungeon diagnostic tests are
read-only. A separate explicitly confirmed action can request exactly one
AutoDuty run; no scheduler, fishing, retainer automation, automatic retry, or
automatic duty abandonment is implemented. No tests run automatically on load.

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
open the main window; its Diagnostics button opens the separate debug window.

The themed main window shows cached character/tome state, independent dungeon and
mode selections, readiness, the AutoDuty action option, and the separately
confirmed one-run action. A shared slate/cyan ImGui theme is scoped to TT windows
and popups; it uses standard Dalamud fonts/scaling and no added UI library.

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
`/xllog`. Run transitions include a persisted operation ID; full diagnostic
reports are logged at Debug level, without logging routine framework polling.

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

Eligibility policy checks (plain .NET console executable):

```sh
dotnet run --project tests/TomesAndTides.PolicyTests.csproj
```
