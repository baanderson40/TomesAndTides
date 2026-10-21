# Tomes & Tides: Plugin Use Case

## Purpose

Tomes & Tides (TT) coordinates a manually started weekly-farming plan for
weekly-capped tomestones. It distributes dungeon runs across daily periods,
then uses safe gaps for AutoRetainer ventures and Ocean Fishing.

TT is not intended to run unattended forever. The user starts and stops a plan;
TT tracks progress and coordinates enabled work until the weekly goal, finish
deadline, or user stop.

## Weekly tome plan

On Start, TT reads current weekly acquired tomes, live weekly cap, held balance,
and selected dungeon reward. It calculates required runs from current weekly
remaining allowance and expected cap-clamped dungeon payout.

- Plan uses 1–7 daily periods.
- Each period begins at 08:00 UTC. This is 3:00 AM CDT and 2:00 AM CST.
- User selects how many periods to use; early finish is period-based.
- TT distributes required runs across available periods and redistributes
  unfinished work across remaining periods.
- If started after a period is complete, TT waits for the next period boundary.
- At each boundary, TT recalculates from live weekly progress before launching.
- Completed-period progress survives stopping and restarting within the week.
- Weekly reset expires the plan; TT does not automatically start a new week.

Each dungeon is a separate one-run AutoDuty request. TT revalidates eligibility,
provider state, weekly headroom, and held-tome space before each request. TT
waits for matching duty entry, completion, territory exit, and AutoDuty stop
before considering another dungeon. Weekly gains are reconciled after every
run; external gains, delayed updates, reset crossing, and cap-clamped payouts
must not be mistaken for a fixed run count.

## Retainer ventures

When ventures are ready, TT can request AutoRetainer's **single-pass Multi
Mode**. It observes the pass through completion and waits for AutoRetainer idle
before returning to dungeon work. TT does not start continuous Multi Mode or
take over an already-running AutoRetainer session.

## Ocean Fishing

Ocean Fishing uses WahTools Feesh and a user-selected character policy:

- **Current farming character**, or
- **Selected character** from AutoRetainer's registered-character roster.

For an eligible fishing window, TT switches through Lifestream if needed,
requests one WahTools voyage, observes duty completion and provider cleanup,
then returns to the farming character. Fishing character is locked for the
claimed window. Ambiguous launches are observed, not blindly retried.

## Coordination and controls

- Tome work has priority when its period allocation is due.
- Active duties, voyages, and owned AutoRetainer passes are never interrupted.
- When a period's dungeon allocation is complete—or weekly cap is reached—TT
  may continue enabled retainer and fishing work until the next period or Stop.
- Completing the tome plan does not by itself stop an active overall session;
  enabled retainer and fishing work may continue until user Stop or the weekly
  reset/finish boundary.
- **Stop** prevents new operations and lets current owned work reach a safe
  boundary; it does not abandon duties or stop unowned provider work.
- Unknown provider state, changed character, timeout, reload, or ambiguous
  outcome blocks new launches and requires recovery/acknowledgment.

AutoDuty exposes no cross-plugin ownership lock. During a TT-owned run, the user
must keep other integrations from controlling AutoDuty or pushing config
overrides. TT does not call AutoDuty SetConfig, PushConfigOverrides, or
PopConfigOverrides. Bare mode is the default; enabling AutoDuty loop actions can
allow configured termination actions to run.

## Current implementation status

### Implemented

- Themed main window and separate diagnostics.
- Live capped-tome discovery, weekly progress, held balance, reward, and
  eligibility checks.
- Explicitly confirmed AutoDuty one-run action with persisted launch intent,
  at-most-once dispatch, recovery, and lifecycle logs.
- Duty entry/completion/exit and AutoDuty-stopped observation.
- Fresh dispatch-time currency/weekly/storage snapshot and cap-clamped expected
  payout persisted with run record.
- Structured `Pending` / `Verified` / `Unverified` / `ResetCrossed` reward
  outcomes; exact expected delta required for verification.
- Reward reconciliation checks logged-in character, currency item, live weekly
  cap, and reset boundary; partial updates wait for timeout, mismatches block
  verification.
- AutoRetainer busy and Multi Mode must both be inactive before dispatch.
- Lifecycle, validation, reward-delta, recovery, and persistence diagnostics;
  Content IDs are not written to lifecycle logs.
- Persisted 1–7-period weekly tome plan, 08:00 UTC boundaries, period allocation,
  missed-work redistribution, Stop/Start ledger preservation, early finish,
  weekly expiry, and automatic one-run chaining after verified reward.
- Main-window plan confirmation, progress/blocker view, stop-after-current-run,
  and recovery acknowledgment.
- In-game Clyteum Trust run verified with a +50 weekly tomestone gain.

### Gaps: planned, not implemented

- AutoRetainer: readiness rules, single-pass Multi Mode request, owned-pass
  observation/cleanup, and farming-character return.
- Ocean Fishing: character selector and roster, eligible-window scheduling,
  one-voyage WahTools request, voyage/cleanup observation, and return travel.
- Cross-activity coordination, including safe handoff ownership and priority
  when tome work and fishing windows overlap.
- Controller-level tests for persistence failures, reloads, stop boundaries,
  and multi-run dispatch; current automated coverage tests pure UTC allocation,
  run-count, stop/deadline, and idempotent-credit policies.

### Remaining one-run limits relevant to future coordination

- `Completed` means duty lifecycle ended; coordinator requires
  `RewardOutcome == Verified` before continuing. Exact delta still cannot prove
  exclusive attribution if other tome gains occur concurrently.
- Live period-boundary, stop, reload, deadline, and persistence-failure
  acceptance remains required in game. Live provider compatibility also needs
  validation.

The farming coordinator is implemented but not yet fully accepted in game.
AutoRetainer and Ocean Fishing handoffs remain unimplemented.
