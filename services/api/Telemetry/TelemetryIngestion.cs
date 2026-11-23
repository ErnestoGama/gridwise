using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GridWise.Api.Telemetry;

/// <summary>One reading as sent by a meter gateway. Every field is required.</summary>
public sealed record ReadingInput(Guid? MeterId, long? Sequence, DateTimeOffset? ReadingStart, decimal? EnergyKwh);

public sealed record TelemetryBatchRequest(IReadOnlyList<ReadingInput?>? Readings);

/// <summary>A reading that passed validation. <see cref="ReadingStart"/> is in UTC.</summary>
public sealed record ValidReading(int Index, Guid MeterId, long Sequence, DateTimeOffset ReadingStart, decimal EnergyKwh);

/// <summary>A reading already stored for one of the batch's meters.</summary>
public sealed record StoredReading(Guid MeterId, long Sequence, DateTimeOffset ReadingStart, decimal EnergyKwh);

public sealed record ReadingConflict(int Index, Guid MeterId, long Sequence, string Code, string Message);

public sealed record TelemetryBatchResult(
    Guid BatchId, int Received, int Inserted, int Duplicates, IReadOnlyList<ReadingConflict> Conflicts, DateTimeOffset ReceivedAt);

public sealed record IngestionPlan(IReadOnlyList<ValidReading> Inserts, int Duplicates, IReadOnlyList<ReadingConflict> Conflicts);

/// <summary>
/// Validation and deduplication for telemetry batches. Both are pure so they
/// can be tested without a database; the endpoint does the I/O.
/// </summary>
public static class TelemetryIngestion
{
    /// <summary>Upper bound on readings per batch; keeps one request to a bounded transaction.</summary>
    public const int MaxReadingsPerBatch = 1000;

    /// <summary>Request body limit for a batch, generous for <see cref="MaxReadingsPerBatch"/> readings.</summary>
    public const long MaxBodyBytes = 512 * 1024;

    /// <summary>At most this many field errors are returned, so a bad batch cannot produce a huge response.</summary>
    public const int MaxReportedErrors = 50;

    /// <summary>Clock difference allowed between a gateway and the API.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public const string SequenceReused = "sequence_reused";
    public const string IntervalTaken = "interval_taken";

    /// <summary>Every conflict code, as allowed by <c>ck_telemetry_batch_conflicts_code</c>.</summary>
    public static readonly IReadOnlyList<string> ConflictCodes = [SequenceReused, IntervalTaken];

    /// <summary>Human-readable text for a conflict code; stored conflicts keep only the code.</summary>
    public static string ConflictMessage(string code) => code switch
    {
        SequenceReused => "This sequence was already received with a different start or energy.",
        IntervalTaken => "This interval already has a reading with another sequence.",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown conflict code."),
    };

    /// <summary>
    /// SHA-256 (lowercase hex) of the validated readings in batch order. Starts
    /// are already UTC and energy is written without trailing zeros, so
    /// <c>12.5</c> and <c>12.500</c>, or the same instant with another offset,
    /// give the same fingerprint. Used to tell a retry from a different batch
    /// sent under the same idempotency key.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<ValidReading> readings)
    {
        var text = new StringBuilder(readings.Count * 80);
        foreach (var r in readings)
        {
            text.Append(r.MeterId.ToString("D"))
                .Append('|').Append(r.Sequence.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(r.ReadingStart.UtcTicks.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(r.EnergyKwh.ToString("0.###", CultureInfo.InvariantCulture))
                .Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Checks every reading against the rules and against the caller's meters.
    /// <paramref name="meterIntervals"/> holds only meters of the caller's
    /// organization, so another tenant's meter is reported as not found.
    /// </summary>
    public static (Dictionary<string, string[]> Errors, List<ValidReading> Readings) Validate(
        TelemetryBatchRequest request, IReadOnlyDictionary<Guid, int> meterIntervals, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();
        var valid = new List<ValidReading>();

        if (request.Readings is null || request.Readings.Count == 0)
        {
            errors["readings"] = ["At least one reading is required."];
            return (errors, valid);
        }
        if (request.Readings.Count > MaxReadingsPerBatch)
        {
            errors["readings"] = [$"A batch holds at most {MaxReadingsPerBatch} readings."];
            return (errors, valid);
        }

        for (var i = 0; i < request.Readings.Count && errors.Count < MaxReportedErrors; i++)
        {
            var prefix = $"readings[{i}]";
            var input = request.Readings[i];
            if (input is null)
            {
                errors[prefix] = ["Reading is required."];
                continue;
            }

            var before = errors.Count;
            int? interval = null;
            if (input.MeterId is not { } meterId || meterId == Guid.Empty)
            {
                errors[$"{prefix}.meterId"] = ["Meter is required."];
            }
            else if (meterIntervals.TryGetValue(meterId, out var minutes))
            {
                interval = minutes;
            }
            else
            {
                errors[$"{prefix}.meterId"] = ["Meter not found."];
            }

            if (input.Sequence is not { } sequence)
            {
                errors[$"{prefix}.sequence"] = ["Sequence is required."];
            }
            else if (sequence < 0)
            {
                errors[$"{prefix}.sequence"] = ["Sequence must be zero or greater."];
            }

            DateTimeOffset? startUtc = input.ReadingStart?.ToUniversalTime();
            if (startUtc is null)
            {
                errors[$"{prefix}.readingStart"] = ["Reading start is required."];
            }
            else if (interval is { } m && !ReadingRules.IsAlignedStart(startUtc.Value, m))
            {
                errors[$"{prefix}.readingStart"] = [$"Reading start must fall on a {m}-minute boundary."];
            }
            else if (interval is { } length && startUtc.Value.AddMinutes(length) > now + ClockSkew)
            {
                errors[$"{prefix}.readingStart"] = ["Reading interval has not ended yet."];
            }

            if (input.EnergyKwh is not { } kwh)
            {
                errors[$"{prefix}.energyKwh"] = ["Energy is required."];
            }
            else if (!ReadingRules.IsValidEnergy(kwh))
            {
                errors[$"{prefix}.energyKwh"] = ["Energy must be between 0 and 999999999.999 kWh with at most three decimals."];
            }

            if (errors.Count == before)
            {
                valid.Add(new ValidReading(i, input.MeterId!.Value, input.Sequence!.Value, startUtc!.Value, input.EnergyKwh!.Value));
            }
        }
        return (errors, valid);
    }

    /// <summary>
    /// Splits valid readings into new rows, duplicates and conflicts. Readings
    /// are immutable, so a resent reading is only accepted when it is identical:
    /// <list type="bullet">
    /// <item>same meter, sequence, start and energy as a stored or earlier reading → duplicate, skipped;</item>
    /// <item>same meter and sequence, different start or energy → <c>sequence_reused</c>;</item>
    /// <item>same meter and start, different sequence → <c>interval_taken</c>.</item>
    /// </list>
    /// The first reading in the batch wins over later ones.
    /// </summary>
    public static IngestionPlan Plan(IReadOnlyList<ValidReading> readings, IEnumerable<StoredReading> existing)
    {
        var bySequence = new Dictionary<(Guid, long), StoredReading>();
        var byStart = new Dictionary<(Guid, DateTimeOffset), StoredReading>();
        foreach (var stored in existing)
        {
            bySequence.TryAdd((stored.MeterId, stored.Sequence), stored);
            byStart.TryAdd((stored.MeterId, stored.ReadingStart), stored);
        }

        var inserts = new List<ValidReading>();
        var conflicts = new List<ReadingConflict>();
        var duplicates = 0;
        foreach (var reading in readings)
        {
            if (bySequence.TryGetValue((reading.MeterId, reading.Sequence), out var same))
            {
                if (same.ReadingStart == reading.ReadingStart && same.EnergyKwh == reading.EnergyKwh)
                {
                    duplicates++;
                }
                else
                {
                    conflicts.Add(new ReadingConflict(reading.Index, reading.MeterId, reading.Sequence, SequenceReused,
                        ConflictMessage(SequenceReused)));
                }
                continue;
            }
            if (byStart.ContainsKey((reading.MeterId, reading.ReadingStart)))
            {
                conflicts.Add(new ReadingConflict(reading.Index, reading.MeterId, reading.Sequence, IntervalTaken,
                    ConflictMessage(IntervalTaken)));
                continue;
            }

            var accepted = new StoredReading(reading.MeterId, reading.Sequence, reading.ReadingStart, reading.EnergyKwh);
            bySequence[(reading.MeterId, reading.Sequence)] = accepted;
            byStart[(reading.MeterId, reading.ReadingStart)] = accepted;
            inserts.Add(reading);
        }
        return new IngestionPlan(inserts, duplicates, conflicts);
    }
}
