"""Deterministic synthetic smart-meter data for GridWise.

Produces three files in an output directory:

- ``sites.json``: organizations, buildings (with IANA time zones) and meters
- ``readings.csv``: interval kWh readings, one row per meter and interval
- ``manifest.json``: fixture version, parameters, counts and injected edge cases

Readings are generated in UTC. Load profiles use the building's local time, so
daylight-saving transitions inside the period shift the occupied hours in UTC
the same way they would for a real building.
"""

from __future__ import annotations

import csv
import hashlib
import io
import json
import random
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

FIXTURE_VERSION = "1"
DEFAULT_SEED = 2208
DEFAULT_REFERENCE_DATE = date(2026, 3, 2)  # a Monday; America/Chicago switches to DST on 2026-03-08
DEFAULT_DAYS = 14
DEFAULT_INTERVAL_MINUTES = 15
ALLOWED_INTERVALS = (5, 10, 15, 30, 60)

SERIAL_PREFIX = "GW-"

ORG_WORDS = ("Amber", "Cobalt", "Juniper", "Larch", "Saffron", "Tidewater")
ORG_SUFFIXES = ("Facilities", "Properties", "Estates")
BUILDING_WORDS = ("Harbor", "Meadow", "Quarry", "Orchard", "Lantern", "Willow")
BUILDING_SUFFIXES = ("Hall", "Annex", "Works", "House")
TIME_ZONES = ("Europe/Madrid", "America/Bogota", "America/Chicago", "Europe/London")

# Peak demand range in kW per meter kind.
METER_KINDS = {
    "main": (40.0, 120.0),
    "hvac": (20.0, 60.0),
}

OCCUPIED_START_HOUR = 7
OCCUPIED_END_HOUR = 19
NOISE = 0.05

EDGE_CASE_CHECKS = {
    "duplicate_reading": "unique (meter_id, sequence) on ingestion",
    "telemetry_gap": "gap detection (missing intervals flagged, not zero-filled)",
    "spike": "spike rule against the meter's recent readings",
    "negative_value": "non-negative kWh validation",
    "clock_skew": "interval alignment check",
    "overnight_anomaly": "overnight consumption rule against the baseline",
}

GAP_INTERVALS = 8
SPIKE_FACTOR = 25


@dataclass(frozen=True)
class Options:
    seed: int = DEFAULT_SEED
    reference_date: date = DEFAULT_REFERENCE_DATE
    days: int = DEFAULT_DAYS
    interval_minutes: int = DEFAULT_INTERVAL_MINUTES
    edge_cases: bool = True

    def __post_init__(self) -> None:
        if self.interval_minutes not in ALLOWED_INTERVALS:
            raise ValueError(f"interval_minutes must be one of {ALLOWED_INTERVALS}")
        if not 3 <= self.days <= 366:
            raise ValueError("days must be between 3 and 366")

    @property
    def intervals_per_meter(self) -> int:
        return self.days * 24 * 60 // self.interval_minutes

    @property
    def start(self) -> datetime:
        return datetime(
            self.reference_date.year,
            self.reference_date.month,
            self.reference_date.day,
            tzinfo=timezone.utc,
        )


@dataclass
class Meter:
    id: str
    serial: str
    kind: str
    building_id: str
    base_kw: float
    peak_kw: float


@dataclass
class FixtureSet:
    options: Options
    organizations: list[dict] = field(default_factory=list)
    buildings: list[dict] = field(default_factory=list)
    meters: list[Meter] = field(default_factory=list)
    readings: list[tuple[str, int, str, str]] = field(default_factory=list)
    edge_cases: list[dict] = field(default_factory=list)


def format_ts(ts: datetime) -> str:
    return ts.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def load_factor(local: datetime) -> float:
    """Share of the gap between base and peak load drawn at a local time."""
    hour = local.hour + local.minute / 60
    weekday = local.weekday() < 5
    if OCCUPIED_START_HOUR <= hour < OCCUPIED_END_HOUR:
        return 1.0 if weekday else 0.15
    if OCCUPIED_START_HOUR - 1 <= hour < OCCUPIED_START_HOUR or OCCUPIED_END_HOUR <= hour < OCCUPIED_END_HOUR + 1:
        return 0.5 if weekday else 0.1
    return 0.0


def _build_sites(rng: random.Random, fx: FixtureSet) -> None:
    org_names = rng.sample([f"{w} {s}" for w in ORG_WORDS for s in ORG_SUFFIXES], 2)
    building_names = rng.sample([f"{w} {s}" for w in BUILDING_WORDS for s in BUILDING_SUFFIXES], 4)
    zones = list(TIME_ZONES)
    serials = rng.sample(range(100000, 1000000), 8)

    for o, org_name in enumerate(org_names, start=1):
        org_id = f"org-{o:02d}"
        fx.organizations.append({"id": org_id, "name": org_name})
        for b in range(2):
            index = (o - 1) * 2 + b
            building_id = f"bld-{index + 1:02d}"
            fx.buildings.append(
                {
                    "id": building_id,
                    "organization_id": org_id,
                    "name": building_names[index],
                    "time_zone": zones[index],
                    "floor_area_m2": rng.randrange(1500, 12000, 100),
                }
            )
            for kind in METER_KINDS:
                low, high = METER_KINDS[kind]
                peak = round(rng.uniform(low, high), 1)
                base = round(peak * rng.uniform(0.15, 0.3), 1)
                n = len(fx.meters)
                fx.meters.append(
                    Meter(
                        id=f"mtr-{n + 1:02d}",
                        serial=f"{SERIAL_PREFIX}{serials[n]}",
                        kind=kind,
                        building_id=building_id,
                        base_kw=base,
                        peak_kw=peak,
                    )
                )


def _overnight_sequences(opts: Options, tz: ZoneInfo, night: date) -> list[int]:
    """Sequences from 23:00 local on ``night`` to 05:00 local the next morning."""
    first = datetime(night.year, night.month, night.day, 23, tzinfo=tz)
    last = first + timedelta(hours=6)
    step = timedelta(minutes=opts.interval_minutes)
    seqs = []
    for seq in range(opts.intervals_per_meter):
        ts = opts.start + seq * step
        if first <= ts < last:
            seqs.append(seq)
    return seqs


def _plan_edge_cases(rng: random.Random, fx: FixtureSet) -> dict[str, dict]:
    """Pick a distinct meter and position for each injected problem."""
    opts = fx.options
    n = opts.intervals_per_meter
    meters = rng.sample(fx.meters, len(EDGE_CASE_CHECKS))
    plan: dict[str, dict] = {}
    # Keep injected problems away from the first and last day.
    per_day = n // opts.days
    lo, hi = per_day, n - per_day - GAP_INTERVALS

    for kind, meter in zip(EDGE_CASE_CHECKS, meters):
        entry = {"type": kind, "meter_id": meter.id, "caught_by": EDGE_CASE_CHECKS[kind]}
        if kind == "overnight_anomaly":
            tz = ZoneInfo(next(b["time_zone"] for b in fx.buildings if b["id"] == meter.building_id))
            weeknights = [
                opts.reference_date + timedelta(days=d)
                for d in range(1, opts.days - 1)
                if (opts.reference_date + timedelta(days=d)).weekday() < 4
            ]
            seqs = _overnight_sequences(opts, tz, rng.choice(weeknights))
            entry.update(sequence=seqs[0], count=len(seqs))
        elif kind == "telemetry_gap":
            entry.update(sequence=rng.randrange(lo, hi), count=GAP_INTERVALS)
        elif kind == "clock_skew":
            entry.update(sequence=rng.randrange(lo, hi), skew_seconds=rng.randrange(60, opts.interval_minutes * 30))
        else:
            entry.update(sequence=rng.randrange(lo, hi))
        plan[kind] = entry
    return plan


def generate(opts: Options | None = None) -> FixtureSet:
    opts = opts or Options()
    rng = random.Random(opts.seed)
    fx = FixtureSet(options=opts)
    _build_sites(rng, fx)

    # Edge cases draw from their own stream so that every reading they do not
    # touch is identical with and without them.
    plan = _plan_edge_cases(random.Random(f"{opts.seed}:edge-cases"), fx) if opts.edge_cases else {}
    by_meter: dict[str, list[dict]] = {}
    for entry in plan.values():
        by_meter.setdefault(entry["meter_id"], []).append(entry)

    zones = {b["id"]: ZoneInfo(b["time_zone"]) for b in fx.buildings}
    step = timedelta(minutes=opts.interval_minutes)
    hours = opts.interval_minutes / 60

    for meter in fx.meters:
        tz = zones[meter.building_id]
        cases = by_meter.get(meter.id, [])
        gap = anomaly = range(0)
        point: dict[int, dict] = {}
        for c in cases:
            span = range(c["sequence"], c["sequence"] + c.get("count", 1))
            if c["type"] == "telemetry_gap":
                gap = span
            elif c["type"] == "overnight_anomaly":
                anomaly = span
            else:
                point[c["sequence"]] = c

        for seq in range(opts.intervals_per_meter):
            ts = opts.start + seq * step
            factor = 0.9 if seq in anomaly else load_factor(ts.astimezone(tz))
            kw = meter.base_kw + (meter.peak_kw - meter.base_kw) * factor
            kwh = kw * hours * (1 + rng.uniform(-NOISE, NOISE))
            if seq in gap:
                continue  # missing data stays missing; never written as 0
            c = point.get(seq)
            if c and c["type"] == "spike":
                kwh *= SPIKE_FACTOR
            elif c and c["type"] == "negative_value":
                kwh = -kwh
            elif c and c["type"] == "clock_skew":
                ts += timedelta(seconds=c["skew_seconds"])
            row = (meter.id, seq, format_ts(ts), f"{kwh:.3f}")
            fx.readings.append(row)
            if c and c["type"] == "duplicate_reading":
                fx.readings.append(row)

    fx.edge_cases = sorted(plan.values(), key=lambda e: (e["meter_id"], e["sequence"]))
    return fx


def _json(data: object) -> str:
    return json.dumps(data, indent=2, sort_keys=True, ensure_ascii=False) + "\n"


def render(fx: FixtureSet) -> dict[str, str]:
    """Return file name -> file contents for a generated set."""
    opts = fx.options
    sites = {
        "organizations": fx.organizations,
        "buildings": fx.buildings,
        "meters": [
            {
                "id": m.id,
                "serial": m.serial,
                "kind": m.kind,
                "building_id": m.building_id,
                "base_kw": m.base_kw,
                "peak_kw": m.peak_kw,
            }
            for m in fx.meters
        ],
    }
    buf = io.StringIO()
    writer = csv.writer(buf, lineterminator="\n")
    writer.writerow(("meter_id", "sequence", "reading_start", "kwh"))
    writer.writerows(fx.readings)

    files = {"sites.json": _json(sites), "readings.csv": buf.getvalue()}
    manifest = {
        "fixture_version": FIXTURE_VERSION,
        "seed": opts.seed,
        "reference_date": opts.reference_date.isoformat(),
        "days": opts.days,
        "interval_minutes": opts.interval_minutes,
        "period_start": format_ts(opts.start),
        "period_end": format_ts(opts.start + timedelta(days=opts.days)),
        "counts": {
            "organizations": len(fx.organizations),
            "buildings": len(fx.buildings),
            "meters": len(fx.meters),
            "readings": len(fx.readings),
        },
        "edge_cases": fx.edge_cases,
        "files": {
            name: hashlib.sha256(body.encode("utf-8")).hexdigest() for name, body in sorted(files.items())
        },
    }
    files["manifest.json"] = _json(manifest)
    return files


def write(fx: FixtureSet, out: Path) -> list[Path]:
    out.mkdir(parents=True, exist_ok=True)
    written = []
    for name, body in render(fx).items():
        path = out / name
        path.write_text(body, encoding="utf-8", newline="")
        written.append(path)
    return written
