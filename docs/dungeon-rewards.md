# Phase 1: dungeon rewards

## Decision

Use the installed game's Lumina sheets to discover capped-tomestone dungeon
rewards. A hardcoded dungeon/reward catalog is not needed for the verified
snapshot. The Python audit is an offline proof of the data joins, not a runtime
dependency or a plugin implementation.

## Evidence and provenance

Reference corpus: adjacent `ffxiv-knowledge/` directory.

CSV source: `external/ffxiv-datamining`, commit `76d22160d`, dated 2026-10-13,
commit subject `7.57 (#120)`.

This validates a local extracted snapshot, not a live game session. Production
must use installed game data and the live client weekly limit.

## Currency resolution

1. Find a `Tomestones` row with `WeeklyLimit > 0`.
2. Join `TomestonesItem.Tomestones` to that row's ID.
3. Resolve the currency's item through `TomestonesItem.Item` and `Item`.
4. Map category 1 to currency A, 2 to B, and 3 to C.

In this snapshot:

| Item | Name | Tomestones category | Reward column | Sheet weekly limit |
|---:|---|---:|---|---:|
| 28 | Allagan Tomestone of Poetics | 1 | A | 0 |
| 48 | Allagan Tomestone of Mathematics | 2 | B | 0 |
| 49 | Allagan Tomestone of Mnemonics | 3 | C | 900 |

The category mapping is independently documented in ClientStructs:
`platform/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentContentsFinderInterface.cs`,
`RewardsData.CurrencyA/B/C` remarks.

Do not confuse the `TomestonesItem` row ID (23 for Mnemonics), item ID (49),
Tomestones category (3), or currency inventory slot (5).

For the live planner, use `InventoryManager.GetLimitedTomestoneWeeklyLimit()`
and `InventoryManager.Instance()->GetWeeklyAcquiredTomestoneCount()` rather
than hardcoding the snapshot's 900 or an older 450 cap.

The tomestone **held** limit is separate from the weekly limit. The current game
rule is 2,000 of each tomestone (reference: [Allagan Tomestones, FFXIV Wiki](https://ffxiv.consolegameswiki.com/wiki/Allagan_Tomestones));
the official Lodestone UI guide confirms a held limit exists but does not give
its numeric value. The client sheets/API inspected here expose the weekly limit
and current balance, but no numeric per-currency holding-limit field. Runtime
uses a named 2,000 rule constant with this provenance, and should remain easy to
revise if Square Enix changes the universal holding rule.

## Dungeon join and reward formula

Select `ContentFinderCondition` rows where `ContentType == 2` (dungeon) and
`ContentLinkType == 1` (InstanceContent). Join `Content` to `InstanceContent`.

For the resolved category, calculate:

```text
repeatable reward = FinalBossCurrencyX + sum(BossCurrencyX[0..4])
```

Exclude new-player bonuses, roulette bonuses, and other conditional rewards.
Do not filter by English names, Expert roulette membership, or a hardcoded level.
Keep ContentFinderCondition ID, InstanceContent ID, and TerritoryType ID separate.
AutoDuty's Run and ContentHasPath IPCs take the territory ID.

## Verified dungeon results

All eight supplied Mnemonics and Mathematics totals match the sheets.
For these duties, all Mnemonics are assigned to the final boss; the preceding
bosses grant zero Mnemonics. Mathematics totals include earlier bosses.

| Dungeon | Territory | CFC | Instance | Mathematics | Mnemonics | Support / Trust sheet evidence | Local AD path |
|---|---:|---:|---:|---:|---:|---|---|
| Alexandria | 1199 | 827 | 95 | 50 | 20 | Yes / Yes | Yes |
| Tender Valley | 1203 | 834 | 98 | 50 | 20 | No / No | No |
| The Strayborough Deadwalk | 1204 | 981 | 99 | 50 | 20 | No / No | No |
| Yuweyawata Field Station | 1242 | 1008 | 100 | 50 | 20 | Yes / Yes | Yes |
| The Underkeep | 1266 | 1027 | 101 | 60 | 30 | Yes / Yes | Yes |
| The Meso Terminal | 1292 | 1028 | 102 | 60 | 30 | Yes / Yes | Yes |
| Mistwake | 1314 | 1064 | 103 | 80 | 50 | Yes / Yes | Yes |
| The Clyteum | 1345 | 1011 | 104 | 80 | 50 | Yes / Yes | Yes |

The initial Support/Trust picker can therefore offer six candidates. The two
optional dungeons are reward-bearing but not eligible for these solo NPC modes.
Local path presence is snapshot evidence only; query the installed AutoDuty
provider at runtime.

## Support and Trust evidence

AutoDuty source: `reference-plugins/AutoDuty/AutoDuty/Helpers/ContentHelper.cs`.

- Join `DawnContent.Content` to ContentFinderCondition ID.
- Support evidence: matching DawnContent with more than one
  `DawnContentParticipable` subrow. AutoDuty also computes a usable Dawn index.
- Trust evidence: AutoDuty derives its Trust list from `DawnContent.Unknown13`
  and computes an expansion-specific index.

These are availability hints, not proof of character eligibility. Runtime checks
still need unlock status, combat job level, item level, selected mode, installed
path, Trust party levels, and the provider's ability to run the selected duty.
Do not expose the unknown sheet field as a stable public API or copy AutoDuty's
expansion-index arithmetic without checking compatibility.

## Production implementation boundary

The future C# catalog should:

1. Read current typed sheets through Dalamud/Lumina, not these CSVs.
2. Resolve the one supported capped category and currency item.
3. Discover all positive-reward dungeon rows and sum boss rewards.
4. Resolve localized names and carry all three duty identifiers.
5. Separate reward discovery from mode/path/character eligibility.
6. Query installed AutoDuty `ContentHasPath(territoryId)` before offering a run.
7. Use the live weekly acquired count and limit for scheduling.
8. Reconcile observed progress after each run. Account for the final cap-clamped
   payout, outside gains, delayed updates, and a weekly reset during a run.
9. Rebuild/revalidate data after currency/game-data changes.

If no unique capped mapping exists, a required sheet/column is absent, or the
category is unsupported, block farming with a diagnostic. Never silently reuse
old rewards or assume item 49 forever.

## Outstanding validation

- Compile against installed Lumina sheet types; the offline audit uses CSV names.
- Verify repeatable reward totals and live weekly counter deltas in-game.
- Verify selected-mode eligibility against installed AutoDuty, not just path files.
- Verify current unlock and Trust-level checks before automated queueing.
- Retain the Python audit/tests as reproducible evidence; they do not replace
  runtime eligibility or live integration testing.
