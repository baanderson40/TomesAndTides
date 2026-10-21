"""Read-only offline audit; production will read these sheets through Lumina.

Print JSON to stdout. No game, provider, or repository files are modified.
"""

import argparse
import csv
import json
from collections import Counter
from pathlib import Path


def read_sheet(directory, name):
    with (directory / f"{name}.csv").open(encoding="utf-8-sig", newline="") as source:
        return list(csv.DictReader(source))


def resolve_capped_currency(tomestones, mappings, items):
    limits = {int(row["#"]): int(row["WeeklyLimit"]) for row in tomestones}
    candidates = [
        row for row in mappings
        if limits.get(int(row["Tomestones"]), 0) > 0
    ]
    if len(candidates) != 1:
        raise ValueError(f"Expected one capped currency mapping, found {len(candidates)}")
    mapping = candidates[0]
    category = int(mapping["Tomestones"])
    if category not in (1, 2, 3):
        raise ValueError(f"Unsupported dungeon currency category: {category}")
    item_id = int(mapping["Item"])
    item = next(row for row in items if int(row["#"]) == item_id)
    return {
        "item_id": item_id,
        "name": item["Name"],
        "tomestones_category": category,
        "currency_column": {1: "A", 2: "B", 3: "C"}[category],
        "weekly_limit_from_sheet": limits[category],
    }


def reward_breakdown(instance, column):
    if column not in ("A", "B", "C"):
        raise ValueError(f"Unsupported currency column: {column}")
    final_boss = int(instance[f"FinalBossCurrency{column}"])
    bosses = [int(instance[f"BossCurrency{column}[{index}]"]) for index in range(5)]
    if min([final_boss] + bosses) < 0:
        raise ValueError("Negative dungeon reward")
    return {
        "boss_rewards": bosses,
        "final_boss_reward": final_boss,
        "total": final_boss + sum(bosses),
    }


def discover_dungeons(conditions, instances, dawn, participants, column, paths=None):
    instance_by_id = {int(row["#"]): row for row in instances}
    dawn_by_content = {int(row["Content"]): row for row in dawn}
    participant_counts = Counter(int(row["#"].split(".")[0]) for row in participants)
    result = []
    for condition in conditions:
        # ContentType 2 = dungeon; ContentLinkType 1 = InstanceContent.
        if int(condition["ContentType"]) != 2 or int(condition["ContentLinkType"]) != 1:
            continue
        instance_id = int(condition["Content"])
        instance = instance_by_id.get(instance_id)
        if instance is None:
            raise ValueError(f"Missing InstanceContent {instance_id}")
        reward = reward_breakdown(instance, column)
        if reward["total"] <= 0:
            continue
        condition_id = int(condition["#"])
        territory_id = int(condition["TerritoryType"])
        dawn_row = dawn_by_content.get(condition_id)
        support = dawn_row is not None and participant_counts[int(dawn_row["#"])] > 1
        # This is sheet evidence used by AutoDuty, NOT proof that a character
        # has unlocked the duty or has a sufficiently leveled Trust party.
        trust = dawn_row is not None and dawn_row["Unknown13"].lower() == "true"
        path_names = None if paths is None else sorted(
            path.name for path in paths.glob(f"({territory_id})*.json")
        )
        result.append({
            "name": condition["Name"],
            "content_finder_condition_id": condition_id,
            "instance_content_id": instance_id,
            "territory_id": territory_id,
            "required_level": int(condition["ClassJobLevelRequired"]),
            "required_item_level": int(condition["ItemLevelRequired"]),
            "reward": reward,
            "duty_support_sheet_evidence": support,
            "trust_sheet_evidence": trust,
            "local_autoduty_path_files": path_names,
        })
    return sorted(result, key=lambda row: (row["required_item_level"], row["territory_id"]))


def audit(sheet_directory, paths=None):
    currency = resolve_capped_currency(
        read_sheet(sheet_directory, "Tomestones"),
        read_sheet(sheet_directory, "TomestonesItem"),
        read_sheet(sheet_directory, "Item"),
    )
    return {
        "sheet_directory": str(sheet_directory.resolve()),
        "autoduty_paths_directory": None if paths is None else str(paths.resolve()),
        "currency": currency,
        "dungeons": discover_dungeons(
            read_sheet(sheet_directory, "ContentFinderCondition"),
            read_sheet(sheet_directory, "InstanceContent"),
            read_sheet(sheet_directory, "DawnContent"),
            read_sheet(sheet_directory, "DawnContentParticipable"),
            currency["currency_column"],
            paths,
        ),
    }


def main():
    corpus = Path(__file__).resolve().parents[2] / "ffxiv-knowledge"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--sheets", type=Path,
        default=corpus / "external/ffxiv-datamining/csv/en",
    )
    parser.add_argument(
        "--autoduty-paths", type=Path,
        default=corpus / "reference-plugins/AutoDuty/AutoDuty/Paths",
    )
    args = parser.parse_args()
    if not args.autoduty_paths.is_dir():
        parser.error(f"AutoDuty paths directory not found: {args.autoduty_paths}")
    try:
        output = audit(args.sheets, args.autoduty_paths)
    except (OSError, KeyError, ValueError, StopIteration) as error:
        parser.exit(1, f"Audit failed: {error}\n")
    print(json.dumps(output, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
