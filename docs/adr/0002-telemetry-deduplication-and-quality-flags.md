# 0002. Telemetry deduplication and quality flags

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Meter gateways retry. A batch can arrive twice because a response was lost,
a gateway can restart and resend its last hour, and a faulty gateway can send
a different value for an interval that is already stored. Readings drive cost
reports, baselines and anomaly rules, so a double-counted or silently replaced
interval gives wrong numbers further down.

Readings are already immutable (`meter_readings` rejects `UPDATE`) and keyed
by `(meter_id, reading_start)`. TimescaleDB needs the time column in every
unique index, so `(meter_id, sequence)` cannot be a database constraint.

Two further problems follow in this session: intervals that never arrive, and
values that are implausible for the meter. Both need to be visible without
changing stored readings.

## Decision

### Deduplication

1. **Readings stay immutable.** An identical resend (same meter, sequence,
   start and energy) is counted as a duplicate and skipped. A reading that
   contradicts a stored one (`sequence_reused`, `interval_taken`) is not
   stored and is reported as a conflict; the rest of the batch is stored.
2. **Every accepted batch is recorded** in `telemetry_batches` with its counts
   and in `telemetry_batch_conflicts` with one row per conflicting reading
   (index, meter, sequence, code). Rows are written once in the same
   transaction as the readings, so the history always matches what was stored.
   Rejected (`422`) batches store nothing.
3. **Optional `Idempotency-Key` header.** The key is unique per organization
   (partial unique index). A retry with the same key and the same readings
   returns the original result with `Idempotent-Replayed: true` and does no
   work. The same key with different readings gets `409
   idempotency_key_reused`. Readings are compared by a SHA-256 fingerprint of
   the normalized readings (UTC starts, energy without trailing zeros), so a
   retry that only changes formatting still matches.
4. **Locking.** A transaction-scoped advisory lock on the key is taken before
   the per-meter locks, and meter locks are taken in ID order, so concurrent
   batches cannot deadlock each other.
5. **Who can see it.** Sending batches stays admin only. Analysts and above can
   list batches (newest first, cursor pages) and open one with its conflicts.
   Keys, reading values and serials are never logged.

### Quality flags

6. **Flags are rows, readings stay untouched.** Gaps and spikes are stored in
   `reading_quality_flags` (meter, kind, interval start/end, rule version). A
   gap marks intervals as unknown; it is never filled with zero. The table
   references the meter, not individual readings, so `meter_readings` stays an
   append-only hypertable.
7. **Plain, deterministic rules** (rule version 1):
   - *Gap:* one flag per run of missing intervals between two stored
     readings. Nothing after the last reading is flagged, because the next
     reading may simply not have arrived yet.
   - *Spike:* a reading at least 4× the median of the meter's readings in the
     previous 24 hours and at least 1 kWh above it. The median ignores the odd
     outlier in the baseline; the 1 kWh floor keeps an idle meter at night
     from flagging every small load. With fewer than 8 readings in the window
     there is no baseline and no spike.
8. **Recomputed on ingestion.** After a batch stores new readings, flags are
   recomputed in the same transaction and under the same meter locks, from
   the stored reading before the new ones to 24 hours after them (the spike
   look-back). Unchanged flags keep their timestamps, changed ones are updated
   and stale ones removed, so a late reading shrinks or clears its gap.
9. **Every member can read flags.** Operators need to know which numbers are
   unknown or suspicious; flags carry no more than the readings they describe.

## Consequences

- Clients get safe retries without a key; the key only adds a guaranteed
  identical response and protection against reusing a key by mistake.
- History grows by one row per batch plus one per conflict. Retention is
  handled with the other retention controls later.
- A conflicting value is never stored, so the original reading wins until an
  operator investigates. The conflict row keeps the evidence (meter, sequence,
  code) but not the rejected energy value.
- The in-memory API tests cannot check advisory locks, partial unique indexes
  or check constraints; these need the TimescaleDB integration suite.
- Ingestion does a few more queries per meter (previous reading, the window
  around the new readings, the next reading, stored flags). A batch is capped
  at 1,000 readings, so the work stays bounded.
- Changing a threshold means bumping the rule version and recomputing flags;
  until then old flags keep the version that produced them.
