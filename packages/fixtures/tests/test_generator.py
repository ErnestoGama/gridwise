"""Acceptance tests AC-01.7 to AC-01.15 for the synthetic meter generator."""

from __future__ import annotations

import csv
import io
import json
import re
import subprocess
import sys
import tempfile
import unittest
from collections import Counter, defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

from gridwise_fixtures import FIXTURE_VERSION, Options, generate, render
from gridwise_fixtures.generator import (
    BUILDING_SUFFIXES,
    BUILDING_WORDS,
    EDGE_CASE_CHECKS,
    ORG_SUFFIXES,
    ORG_WORDS,
)

PACKAGE_ROOT = Path(__file__).resolve().parents[1]
KWH = re.compile(r"^-?\d+\.\d{3}$")


def parse(files: dict[str, str]) -> tuple[dict, list[dict], dict]:
    sites = json.loads(files["sites.json"])
    rows = list(csv.DictReader(io.StringIO(files["readings.csv"])))
    for row in rows:
        row["sequence"] = int(row["sequence"])
        row["ts"] = datetime.strptime(row["reading_start"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    manifest = json.loads(files["manifest.json"])
    return sites, rows, manifest


def injected(manifest: dict) -> set[tuple[str, int]]:
    """(meter_id, sequence) pairs touched by an injected edge case."""
    pairs = set()
    for case in manifest["edge_cases"]:
        for seq in range(case["sequence"], case["sequence"] + case.get("count", 1)):
            pairs.add((case["meter_id"], seq))
    return pairs


def clean_rows(rows: list[dict], manifest: dict) -> list[dict]:
    skip = injected(manifest)
    seen: set[tuple[str, int]] = set()
    clean = []
    for row in rows:
        key = (row["meter_id"], row["sequence"])
        if key in skip or key in seen:
            continue
        seen.add(key)
        clean.append(row)
    return clean


def validate(files: dict[str, str]) -> list[str]:
    """Structural checks every generated set must pass."""
    sites, rows, manifest = parse(files)
    problems = []
    meters = {m["id"] for m in sites["meters"]}
    start = datetime.strptime(manifest["period_start"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    step = timedelta(minutes=manifest["interval_minutes"])
    for row in clean_rows(rows, manifest):
        if row["meter_id"] not in meters:
            problems.append(f"unknown meter {row['meter_id']}")
        if not KWH.match(row["kwh"]) or float(row["kwh"]) < 0:
            problems.append(f"bad kwh {row}")
        if row["ts"] != start + row["sequence"] * step:
            problems.append(f"misaligned {row}")
    if manifest["counts"]["readings"] != len(rows):
        problems.append("reading count does not match manifest")
    return problems


class GeneratorTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.files = render(generate())
        cls.sites, cls.rows, cls.manifest = parse(cls.files)
        cls.clean_files = render(generate(Options(edge_cases=False)))
        cls.clean_sites, cls.clean_rows_, cls.clean_manifest = parse(cls.clean_files)

    def test_ac_01_7_same_seed_byte_identical_files(self) -> None:
        with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b:
            for out in (a, b):
                subprocess.run(
                    [sys.executable, "-m", "gridwise_fixtures", "--out", out],
                    cwd=PACKAGE_ROOT,
                    check=True,
                    capture_output=True,
                )
            for name in ("sites.json", "readings.csv", "manifest.json"):
                self.assertEqual(
                    (Path(a) / name).read_bytes(), (Path(b) / name).read_bytes(), name
                )
                self.assertEqual((Path(a) / name).read_text(encoding="utf-8"), self.files[name])

    def test_ac_01_8_different_seed_different_valid_data(self) -> None:
        other = render(generate(Options(seed=7)))
        self.assertNotEqual(other["readings.csv"], self.files["readings.csv"])
        self.assertEqual(validate(self.files), [])
        self.assertEqual(validate(other), [])

    def test_ac_01_9_one_aligned_reading_per_interval(self) -> None:
        expected = self.manifest["days"] * 24 * 60 // self.manifest["interval_minutes"]
        start = datetime(2026, 3, 2, tzinfo=timezone.utc)
        step = timedelta(minutes=15)
        skip = {(c["meter_id"], c["sequence"]) for c in self.manifest["edge_cases"] if c["type"] == "clock_skew"}
        gaps = {
            (c["meter_id"], s)
            for c in self.manifest["edge_cases"]
            if c["type"] == "telemetry_gap"
            for s in range(c["sequence"], c["sequence"] + c["count"])
        }
        per_meter: dict[str, set[int]] = defaultdict(set)
        for row in self.rows:
            per_meter[row["meter_id"]].add(row["sequence"])
            if (row["meter_id"], row["sequence"]) not in skip:
                self.assertEqual(row["ts"], start + row["sequence"] * step)
                self.assertEqual(row["ts"].minute % 15, 0)
                self.assertEqual(row["ts"].second, 0)
        for meter in self.sites["meters"]:
            missing = {(meter["id"], s) for s in range(expected)} - {(meter["id"], s) for s in per_meter[meter["id"]]}
            self.assertEqual(missing, {g for g in gaps if g[0] == meter["id"]})
        # Without edge cases, every meter has every interval exactly once.
        counts = Counter((r["meter_id"], r["sequence"]) for r in self.clean_rows_)
        self.assertEqual(len(counts), expected * len(self.clean_sites["meters"]))
        self.assertEqual(set(counts.values()), {1})

    def test_ac_01_10_plausible_load_profile(self) -> None:
        zones = {b["id"]: ZoneInfo(b["time_zone"]) for b in self.clean_sites["buildings"]}
        building = {m["id"]: m["building_id"] for m in self.clean_sites["meters"]}
        occupied: dict[str, list[float]] = defaultdict(list)
        overnight: dict[str, list[float]] = defaultdict(list)
        for row in self.clean_rows_:
            self.assertRegex(row["kwh"], KWH)
            value = float(row["kwh"])
            self.assertGreaterEqual(value, 0)
            local = row["ts"].astimezone(zones[building[row["meter_id"]]])
            if local.weekday() < 5 and 9 <= local.hour < 17:
                occupied[row["meter_id"]].append(value)
            elif local.hour < 5:
                overnight[row["meter_id"]].append(value)
        for meter in self.clean_sites["meters"]:
            day = sum(occupied[meter["id"]]) / len(occupied[meter["id"]])
            night = sum(overnight[meter["id"]]) / len(overnight[meter["id"]])
            self.assertGreater(day, night, meter["id"])

    def test_ac_01_10_profile_follows_local_time_across_dst(self) -> None:
        chicago = next(b for b in self.clean_sites["buildings"] if b["time_zone"] == "America/Chicago")
        meter = next(m for m in self.clean_sites["meters"] if m["building_id"] == chicago["id"])
        rows = {r["ts"]: float(r["kwh"]) for r in self.clean_rows_ if r["meter_id"] == meter["id"]}
        # 10:00 local on a weekday is occupied both before (UTC-6) and after (UTC-5) 2026-03-08.
        before = rows[datetime(2026, 3, 5, 16, tzinfo=timezone.utc)]
        after = rows[datetime(2026, 3, 12, 15, tzinfo=timezone.utc)]
        overnight = rows[datetime(2026, 3, 12, 8, tzinfo=timezone.utc)]  # 03:00 local
        self.assertGreater(before, overnight * 2)
        self.assertGreater(after, overnight * 2)

    def test_ac_01_11_sequences_unique_except_listed_duplicates(self) -> None:
        counts = Counter((r["meter_id"], r["sequence"]) for r in self.rows)
        repeated = {key for key, n in counts.items() if n > 1}
        listed = {(c["meter_id"], c["sequence"]) for c in self.manifest["edge_cases"] if c["type"] == "duplicate_reading"}
        self.assertEqual(repeated, listed)
        self.assertEqual({counts[k] for k in repeated}, {2})

    def test_ac_01_12_each_injected_problem_present_and_listed(self) -> None:
        cases = {c["type"]: c for c in self.manifest["edge_cases"]}
        self.assertEqual(set(cases), set(EDGE_CASE_CHECKS))
        self.assertEqual(len(self.manifest["edge_cases"]), len(EDGE_CASE_CHECKS))
        self.assertEqual(len({c["meter_id"] for c in cases.values()}), len(cases))
        for case in cases.values():
            self.assertIn(case["meter_id"], {m["id"] for m in self.sites["meters"]})
            self.assertIsInstance(case["sequence"], int)
            self.assertEqual(case["caught_by"], EDGE_CASE_CHECKS[case["type"]])

        def value(meter_id: str, seq: int) -> float:
            return next(float(r["kwh"]) for r in self.rows if r["meter_id"] == meter_id and r["sequence"] == seq)

        def clean_value(meter_id: str, seq: int) -> float:
            return next(float(r["kwh"]) for r in self.clean_rows_ if r["meter_id"] == meter_id and r["sequence"] == seq)

        spike = cases["spike"]
        self.assertAlmostEqual(
            value(spike["meter_id"], spike["sequence"]), clean_value(spike["meter_id"], spike["sequence"]) * 25, delta=0.05
        )
        neg = cases["negative_value"]
        self.assertLess(value(neg["meter_id"], neg["sequence"]), 0)

        skew = cases["clock_skew"]
        row = next(r for r in self.rows if r["meter_id"] == skew["meter_id"] and r["sequence"] == skew["sequence"])
        aligned = datetime(2026, 3, 2, tzinfo=timezone.utc) + timedelta(minutes=15 * skew["sequence"])
        self.assertEqual(row["ts"] - aligned, timedelta(seconds=skew["skew_seconds"]))
        self.assertNotEqual(row["ts"].minute % 15 * 60 + row["ts"].second, 0)

        night = cases["overnight_anomaly"]
        building = next(m["building_id"] for m in self.sites["meters"] if m["id"] == night["meter_id"])
        tz = ZoneInfo(next(b["time_zone"] for b in self.sites["buildings"] if b["id"] == building))
        seqs = range(night["sequence"], night["sequence"] + night["count"])
        self.assertEqual(night["count"], 24)  # 23:00 to 05:00 local at 15 minutes
        anomalous = [r for r in self.rows if r["meter_id"] == night["meter_id"] and r["sequence"] in seqs]
        normal = [r for r in self.clean_rows_ if r["meter_id"] == night["meter_id"] and r["sequence"] in seqs]
        for row in anomalous:
            local = row["ts"].astimezone(tz)
            self.assertTrue(local.hour >= 23 or local.hour < 5, local)
            self.assertLess(local.weekday() if local.hour >= 23 else local.weekday() - 1, 4)
        self.assertGreater(
            sum(float(r["kwh"]) for r in anomalous), 2 * sum(float(r["kwh"]) for r in normal)
        )

    def test_ac_01_13_gap_readings_absent_not_zero(self) -> None:
        gap = next(c for c in self.manifest["edge_cases"] if c["type"] == "telemetry_gap")
        self.assertGreater(gap["count"], 1)
        seqs = range(gap["sequence"], gap["sequence"] + gap["count"])
        present = [r for r in self.rows if r["meter_id"] == gap["meter_id"] and r["sequence"] in seqs]
        self.assertEqual(present, [])
        neighbours = {gap["sequence"] - 1, gap["sequence"] + gap["count"]}
        found = {r["sequence"] for r in self.rows if r["meter_id"] == gap["meter_id"] and r["sequence"] in neighbours}
        self.assertEqual(found, neighbours)
        zero = [r for r in self.clean_rows_ if float(r["kwh"]) == 0]
        self.assertEqual(zero, [])

    def test_ac_01_14_manifest_records_version_and_counts(self) -> None:
        m = self.manifest
        self.assertEqual(m["fixture_version"], FIXTURE_VERSION)
        self.assertEqual(m["seed"], 2208)
        self.assertEqual(m["reference_date"], "2026-03-02")
        self.assertEqual(m["interval_minutes"], 15)
        self.assertEqual(
            m["counts"],
            {
                "organizations": len(self.sites["organizations"]),
                "buildings": len(self.sites["buildings"]),
                "meters": len(self.sites["meters"]),
                "readings": len(self.rows),
            },
        )
        self.assertEqual(set(m["files"]), {"sites.json", "readings.csv"})

    def test_ac_01_15_plainly_fictional(self) -> None:
        org_names = {f"{w} {s}" for w in ORG_WORDS for s in ORG_SUFFIXES}
        building_names = {f"{w} {s}" for w in BUILDING_WORDS for s in BUILDING_SUFFIXES}
        for org in self.sites["organizations"]:
            self.assertIn(org["name"], org_names)
        for building in self.sites["buildings"]:
            self.assertIn(building["name"], building_names)
            self.assertFalse({"address", "street", "postal_code", "latitude", "longitude"} & set(building))
        for meter in self.sites["meters"]:
            self.assertRegex(meter["serial"], r"^GW-\d{6}$")
        self.assertEqual(len({m["serial"] for m in self.sites["meters"]}), len(self.sites["meters"]))

    def test_rejects_unsupported_options(self) -> None:
        with self.assertRaises(ValueError):
            Options(interval_minutes=7)
        with self.assertRaises(ValueError):
            Options(days=1)
        result = subprocess.run(
            [sys.executable, "-m", "gridwise_fixtures", "--out", "unused", "--interval-minutes", "7"],
            cwd=PACKAGE_ROOT,
            capture_output=True,
        )
        self.assertEqual(result.returncode, 2)

    def test_other_intervals_and_periods(self) -> None:
        for interval in (5, 60):
            files = render(generate(Options(seed=11, days=5, interval_minutes=interval, reference_date=date(2026, 10, 19))))
            self.assertEqual(validate(files), [], interval)


if __name__ == "__main__":
    unittest.main()
