# gridwise-fixtures

Deterministic synthetic smart-meter data for development, tests and the demo, following
[ADR 0001](../../docs/adr/0001-vue-dotnet-stack-and-meter-generator.md).
Python 3.11+, standard library only (time zones come from the system IANA database).

```bash
cd packages/fixtures
python3 -m gridwise_fixtures --out ../../.fixtures             # default seed 2208, reference date 2026-03-02, 14 days
python3 -m gridwise_fixtures --out /tmp/fx --seed 7 --no-edge-cases
python3 -m gridwise_fixtures --out /tmp/fx --days 7 --interval-minutes 60
python3 -m unittest discover -s tests -t .                     # acceptance tests AC-01.7 to AC-01.15
```

The same options always produce byte-identical files.

## Output

| File | Contents |
|---|---|
| `sites.json` | 2 organizations, 4 buildings (each with an IANA time zone) and 8 meters (`main` and `hvac` per building, with base and peak kW) |
| `readings.csv` | `meter_id, sequence, reading_start, kwh`: one row per meter and interval; `reading_start` is UTC, `kwh` is the energy used in that interval with three decimals |
| `manifest.json` | `fixture_version`, seed, reference date, interval, period, counts, injected edge cases and a SHA-256 of each data file |

`sequence` counts intervals from the start of the period, so `reading_start = period_start + sequence × interval`
for every aligned reading.

## Load profile

Each meter draws its base load all the time and its full peak on weekdays from 07:00 to 19:00
in the building's local time, with a half-step in the hour on either side. Weekends stay close
to base load. Every reading gets up to ±5 % noise. Profiles follow local time, so the default
period covers the 2026-03-08 daylight-saving change in `America/Chicago`.

## Edge cases

Added by default (leave them out with `--no-edge-cases`), each on a different meter and listed
in `manifest.json` with the check that must catch it. Readings they do not touch are identical
with and without them.

| Type | What is injected | Caught by |
|---|---|---|
| `duplicate_reading` | the same row sent twice | unique `(meter_id, sequence)` on ingestion |
| `telemetry_gap` | 8 consecutive intervals missing (`count`); never written as `0` | gap detection |
| `spike` | one reading × 25 | spike rule |
| `negative_value` | one negative reading | non-negative kWh validation |
| `clock_skew` | one timestamp moved off the interval boundary (`skew_seconds`) | interval alignment check |
| `overnight_anomaly` | near-peak load from 23:00 to 05:00 local on a weeknight (`count`) | overnight rule against the baseline |

## Fictional data

Organization and building names come from the word lists in `generator.py`, meter serials use the
`GW-` test prefix, and buildings have no addresses or coordinates.

Bump `FIXTURE_VERSION` whenever a change makes existing sets incompatible with the schema or tests.
