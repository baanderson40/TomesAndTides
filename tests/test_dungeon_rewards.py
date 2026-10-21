"""Run with: python3 -B -m unittest discover -s tests -v"""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from audit_dungeon_rewards import audit, discover_dungeons, resolve_capped_currency, reward_breakdown


class RewardPolicyTests(unittest.TestCase):
    def test_resolves_currency_by_category_not_item_or_row_order(self):
        currency = resolve_capped_currency(
            [{"#": "2", "WeeklyLimit": "450"}, {"#": "3", "WeeklyLimit": "0"}],
            [{"Item": "49", "Tomestones": "3"}, {"Item": "999", "Tomestones": "2"}],
            [{"#": "999", "Name": "Future Tome"}],
        )
        self.assertEqual(currency["item_id"], 999)
        self.assertEqual(currency["currency_column"], "B")
        self.assertEqual(currency["weekly_limit_from_sheet"], 450)

    def test_no_capped_currency_fails_closed(self):
        with self.assertRaises(ValueError):
            resolve_capped_currency([], [], [])

    def test_ambiguous_currency_fails_closed(self):
        with self.assertRaises(ValueError):
            resolve_capped_currency(
                [{"#": "3", "WeeklyLimit": "900"}],
                [{"Item": "49", "Tomestones": "3"}] * 2,
                [],
            )

    def test_sums_all_bosses_excluding_new_player_bonus(self):
        row = {"FinalBossCurrencyC": "20", "NewPlayerBonusB": "100"}
        row.update({f"BossCurrencyC[{i}]": str(i + 1) for i in range(5)})
        self.assertEqual(reward_breakdown(row, "C")["total"], 35)

    def test_rejects_unknown_currency_column(self):
        with self.assertRaises(ValueError):
            reward_breakdown({}, "D")

    def test_skips_other_content_types_and_link_types(self):
        rows = [
            {"ContentType": "4", "ContentLinkType": "1"},
            {"ContentType": "2", "ContentLinkType": "2"},
        ]
        self.assertEqual(discover_dungeons(rows, [], [], [], "C"), [])

    def test_missing_instance_fails_closed(self):
        with self.assertRaises(ValueError):
            discover_dungeons(
                [{"ContentType": "2", "ContentLinkType": "1", "Content": "123"}],
                [], [], [], "C",
            )


class LocalSnapshotTests(unittest.TestCase):
    def test_mnemonics_snapshot_matches_supplied_rewards(self):
        corpus = Path(__file__).resolve().parents[2] / "ffxiv-knowledge"
        sheets = corpus / "external/ffxiv-datamining/csv/en"
        if not (sheets / "InstanceContent.csv").is_file():
            self.skipTest("Local game-data reference is not available")
        output = audit(sheets)
        # Snapshot regression evidence, not a production reward catalog.
        self.assertEqual(output["currency"]["item_id"], 49)
        self.assertEqual(output["currency"]["currency_column"], "C")
        expected = {
            1199: (20, True),
            1203: (20, False),
            1204: (20, False),
            1242: (20, True),
            1266: (30, True),
            1292: (30, True),
            1314: (50, True),
            1345: (50, True),
        }
        actual = {row["territory_id"]: row for row in output["dungeons"]}
        self.assertEqual(set(actual), set(expected))
        for territory, (reward, support) in expected.items():
            with self.subTest(territory=territory):
                self.assertEqual(actual[territory]["reward"]["total"], reward)
                self.assertEqual(actual[territory]["duty_support_sheet_evidence"], support)
                self.assertEqual(actual[territory]["trust_sheet_evidence"], support)


if __name__ == "__main__":
    unittest.main()
