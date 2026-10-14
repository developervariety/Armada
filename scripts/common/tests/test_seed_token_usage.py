"""Randomized screenshot data generation without any live API or model calls."""
import datetime
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'seed-token-usage.py'
SPEC = importlib.util.spec_from_file_location('seed_token_usage', SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class SyntheticLoadTests(unittest.TestCase):
    """Bounds, reproducibility, variety, backdating and midnight generation."""

    def test_month_of_irregular_history_preserves_counts_and_has_no_future_records(self):
        now = datetime.datetime(2026, 10, 13, 20, 0, tzinfo=datetime.timezone.utc)
        result = MODULE.generate(42, 8123, now)
        records = result['records']
        dates = [datetime.datetime.fromisoformat(r['CreatedUtc'].replace('Z', '+00:00')) for r in records]
        self.assertGreaterEqual((max(dates).date() - min(dates).date()).days, 40)
        self.assertTrue(all(d <= now for d in dates))
        self.assertEqual(3, len({r['Model'] for r in records}))
        self.assertGreater(len({r['InputTokens'] for r in records}), 100)
        daily = {date.date(): sum(d.date() == date.date() for d in dates) for date in dates}
        self.assertGreater(len(set(daily.values())), 15)
        self.assertTrue(all(r['InputTokens'] >= 0 and r['CachedTokens'] >= 0 and r['OutputTokens'] > 0 for r in records))
        self.assertEqual(result, MODULE.generate(42, 8123, now))
        self.assertNotEqual(records, MODULE.generate(42, 8124, now)['records'])

    def test_default_seed_is_random_and_invalid_ranges_rejected(self):
        now = datetime.datetime(2026, 10, 13, 0, 0, tzinfo=datetime.timezone.utc)
        one = MODULE.generate(31, now=now)
        two = MODULE.generate(31, now=now)
        self.assertNotEqual(one['seed'], two['seed'])
        self.assertTrue(one['records'])
        self.assertLessEqual(datetime.datetime.fromisoformat(one['records'][-1]['CreatedUtc'].replace('Z', '+00:00')), now)
        with self.assertRaises(ValueError):
            MODULE.generate(30)
        with self.assertRaises(ValueError):
            MODULE.generate(366)


if __name__ == '__main__':
    unittest.main()
