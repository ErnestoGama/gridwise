# Week 03 acceptance cases: data quality and live dashboard

Scope: telemetry deduplication and batch history from
[ADR 0002](../adr/0002-telemetry-deduplication-and-quality-flags.md), then gap
and spike flags and the live dashboard. Each case is written as
Given / When / Then.

## Deduplication and batch history

**AC-03.1 — Resend is harmless**
- Given an admin has sent a valid batch
- When the same batch is sent again without a key
- Then nothing new is stored, every reading counts as a duplicate, and a
  second batch row records that outcome

**AC-03.2 — Conflicts are reported, not stored**
- Given a stored reading for a meter and sequence
- When a batch sends the same sequence with another energy, or another
  sequence for the same interval
- Then those readings are listed as `sequence_reused` / `interval_taken` with
  their index, the stored reading is unchanged, and the other readings of the
  batch are stored

**AC-03.3 — Keyed retry returns the original result**
- Given a batch accepted with `Idempotency-Key: k`
- When the same readings are sent again with key `k`
- Then the response is the original result with the same `batchId` and the
  `Idempotent-Replayed: true` header, and no new batch or reading is stored

**AC-03.4 — Reused key is refused**
- Given key `k` was used for a batch
- When different readings are sent with key `k`
- Then the response is `409 idempotency_key_reused` and nothing is stored

**AC-03.5 — Keys are scoped to the organization**
- Given key `k` was used in one organization
- When an admin of another organization sends a batch with key `k`
- Then it is ingested normally

**AC-03.6 — Malformed key**
- When the key is empty, longer than 64 characters or contains characters
  other than letters, digits and `. _ : -`
- Then the response is `422` with an `idempotencyKey` field error

**AC-03.7 — Batch history**
- Given several batches in an organization
- When an analyst lists `/telemetry/batches` with a limit
- Then batches come newest first with a cursor to the next page, and opening a
  batch shows its counts and conflicts

**AC-03.8 — Access**
- Operators get `403` for batch history, non-members `404`, and a batch of
  another organization is `404` even with its ID

## Quality flags

**AC-03.9 — Gaps are unknown, not zero**
- Given a meter with a missing interval between two stored readings
- When quality flags are computed
- Then the missing interval is flagged as a gap and no zero reading is created

**AC-03.10 — Spikes are flagged, readings unchanged**
- Given a reading far above the meter's recent values
- When quality flags are computed
- Then the reading is flagged as a spike and the stored value is unchanged

## Live dashboard

**AC-03.11 — Live updates**
- Given a signed-in member viewing a building dashboard
- When a batch for one of its meters is ingested
- Then the dashboard updates without a page reload, and only members of the
  organization receive the update
