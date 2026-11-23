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
- Given a meter with missing intervals between two stored readings
- When the batch is ingested
- Then one gap flag covers the missing intervals with their count, and no zero
  reading is created

**AC-03.10 — Spikes are flagged, readings unchanged**
- Given at least 8 readings in the meter's previous 24 hours
- When a reading is at least 4× their median and at least 1 kWh above it
- Then the reading is flagged as a spike with its value and baseline, and the
  stored value is unchanged

**AC-03.11 — Late readings update flags**
- Given a gap flag for a meter
- When a later batch fills part of the gap, and then the rest
- Then the gap flag first shrinks to the intervals still missing and is then
  removed; resending a batch leaves flags and their timestamps unchanged

**AC-03.12 — Reading flags**
- Given flags for a meter
- When any member lists `/meters/{id}/quality-flags` with a window, kind or
  limit
- Then flags come in time order with a cursor; a window without an offset or
  an unknown kind is `422`, and a meter of another organization is `404`

## Live dashboard

**AC-03.13 — Live updates**
- Given a signed-in member viewing a building dashboard
- When a batch for one of its meters is ingested
- Then the dashboard updates without a page reload, and only members of the
  organization receive the update

**AC-03.14 — Realtime access**
- Given the telemetry hub
- When a client connects without a session, subscribes to a building of an
  organization it does not belong to, or names another organization's
  building under its own organization
- Then the connection is refused with `401`, and both subscriptions fail with
  the same `not_found` error and receive no updates

**AC-03.15 — Only new readings are pushed**
- Given a client subscribed to a building
- When a batch for its meters stores new readings, then the same batch is
  resent, then the client unsubscribes and another batch arrives
- Then exactly one message arrives, for the first batch, with the stored
  readings grouped by meter

**AC-03.16 — Subscription cap**
- Given a connection following 20 buildings
- When it subscribes to one more
- Then it gets `too_many_subscriptions`; repeating an existing subscription
  does not count, and unsubscribing frees a place

**AC-03.17 — Dashboard initial load**
- Given readings with missing intervals for a building's meters
- When a member opens the live dashboard
- Then every meter shows its readings of the last 24 hours in the building's
  time zone, missing intervals as breaks in the chart and as an unknown
  count (never as zero), and its gap and spike flags; the API answers `422`
  for a window without offsets or longer than 48 hours and `404` for another
  organization's building

**AC-03.18 — Connection states**
- Given a member viewing the live dashboard
- When the connection drops, comes back, or cannot be restored
- Then the page says it is reconnecting, reloads the readings after it
  re-subscribes (messages sent meanwhile are lost), or says the figures may
  be out of date and offers to reconnect
