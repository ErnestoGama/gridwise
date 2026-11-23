using GridWise.Api.Identity;
using GridWise.Api.Sites;

namespace GridWise.Api.Telemetry;

/// <summary>
/// A data-quality finding for one meter: a run of missing intervals (gap) or a
/// reading far above the meter's recent values (spike). Flags sit next to the
/// readings and never change them; a gap is unknown energy, not zero.
/// Flags are derived from stored readings by <see cref="ReadingQuality"/> and
/// are recomputed when new readings arrive around them.
/// </summary>
public sealed class ReadingQualityFlag : ITimestamped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }
    public Guid MeterId { get; set; }

    /// <summary>One of <see cref="ReadingQuality.Kinds"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>UTC start of the first missing interval, or of the spiking reading.</summary>
    public DateTimeOffset IntervalStart { get; set; }

    /// <summary>Exclusive UTC end: the next stored reading for a gap, the end of the interval for a spike.</summary>
    public DateTimeOffset IntervalEnd { get; set; }

    /// <summary>Gap only: how many intervals have no reading.</summary>
    public int? MissingIntervals { get; set; }

    /// <summary>Spike only: sequence of the flagged reading.</summary>
    public long? Sequence { get; set; }

    /// <summary>Spike only: the stored energy of the flagged reading.</summary>
    public decimal? ObservedKwh { get; set; }

    /// <summary>Spike only: median of the meter's readings in the look-back window.</summary>
    public decimal? BaselineKwh { get; set; }

    /// <summary>Version of the rules that produced the flag, so later rule changes can be told apart.</summary>
    public int RuleVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Meter Meter { get; set; } = null!;
}

/// <summary>A flag computed from readings, before it is stored.</summary>
public sealed record QualityFinding(
    string Kind, DateTimeOffset IntervalStart, DateTimeOffset IntervalEnd,
    int? MissingIntervals, long? Sequence, decimal? ObservedKwh, decimal? BaselineKwh);

/// <summary>
/// Gap and spike rules over one meter's stored readings. Pure and
/// deterministic: the same readings always give the same flags, so flags can
/// be recomputed at any time without drifting.
/// </summary>
public static class ReadingQuality
{
    public const string Gap = "gap";
    public const string Spike = "spike";

    /// <summary>Every flag kind, as allowed by <c>ck_reading_quality_flags_kind</c>.</summary>
    public static readonly IReadOnlyList<string> Kinds = [Gap, Spike];

    /// <summary>Bump when a rule or threshold below changes.</summary>
    public const int RuleVersion = 1;

    /// <summary>A spike is compared with the readings of the previous 24 hours.</summary>
    public static readonly TimeSpan SpikeLookback = TimeSpan.FromHours(24);

    /// <summary>Fewer readings than this in the look-back window give no baseline, so no spike is flagged.</summary>
    public const int MinBaselineReadings = 8;

    /// <summary>A reading is a spike when it is at least this many times the baseline…</summary>
    public const decimal SpikeFactor = 4m;

    /// <summary>…and at least this much above it, so near-zero baselines do not flag small loads.</summary>
    public const decimal MinSpikeDeltaKwh = 1m;

    /// <summary>
    /// Flags for readings of one meter. <paramref name="readings"/> must be the
    /// meter's stored readings covering <c>[from - SpikeLookback, to)</c> plus,
    /// if there is one, the first reading at or after <paramref name="to"/>; they
    /// may come in any order. Only flags starting in <c>[from, to)</c> are
    /// returned; earlier readings are context for spike baselines and the
    /// reading after <paramref name="to"/> closes a gap that crosses it. No gap
    /// is reported after the last reading: the meter may simply not have sent
    /// it yet.
    /// </summary>
    public static IReadOnlyList<QualityFinding> Evaluate(
        IEnumerable<StoredReading> readings, int intervalMinutes, DateTimeOffset from, DateTimeOffset to)
    {
        if (!MeterRules.IntervalMinutes.Contains(intervalMinutes))
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes), intervalMinutes, "Unsupported meter interval.");
        }

        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var sorted = readings.OrderBy(r => r.ReadingStart).ToList();
        var findings = new List<QualityFinding>();
        var windowStart = 0;

        for (var i = 0; i < sorted.Count; i++)
        {
            var reading = sorted[i];
            var inRange = reading.ReadingStart >= from && reading.ReadingStart < to;

            if (i > 0)
            {
                var previous = sorted[i - 1];
                var gapStart = previous.ReadingStart + interval;
                if (reading.ReadingStart > gapStart && gapStart >= from && gapStart < to)
                {
                    var missing = (int)((reading.ReadingStart - gapStart).Ticks / interval.Ticks);
                    findings.Add(new QualityFinding(Gap, gapStart, reading.ReadingStart, missing, null, null, null));
                }
            }

            while (sorted[windowStart].ReadingStart < reading.ReadingStart - SpikeLookback)
            {
                windowStart++;
            }
            if (inRange && i - windowStart >= MinBaselineReadings)
            {
                var baseline = Median(sorted.Skip(windowStart).Take(i - windowStart).Select(r => r.EnergyKwh));
                if (IsSpike(reading.EnergyKwh, baseline))
                {
                    findings.Add(new QualityFinding(Spike, reading.ReadingStart, reading.ReadingStart + interval, null,
                        reading.Sequence, reading.EnergyKwh, baseline));
                }
            }
        }

        // Gaps and spikes never share a start (a spike is a stored reading, a gap starts where none is).
        return findings.OrderBy(f => f.IntervalStart).ToList();
    }

    public static bool IsSpike(decimal kwh, decimal baseline) =>
        kwh >= baseline * SpikeFactor && kwh - baseline >= MinSpikeDeltaKwh;

    /// <summary>Median rounded to three decimals like stored energy; the mean of the two middle values for even counts.</summary>
    public static decimal Median(IEnumerable<decimal> values)
    {
        var ordered = values.OrderBy(v => v).ToList();
        if (ordered.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }
        var middle = ordered.Count / 2;
        var median = ordered.Count % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
        return decimal.Round(median, 3, MidpointRounding.AwayFromZero);
    }

    /// <summary>True when a stored flag already says exactly what a new finding says.</summary>
    public static bool Matches(ReadingQualityFlag flag, QualityFinding finding) =>
        flag.Kind == finding.Kind
        && flag.IntervalStart == finding.IntervalStart
        && flag.IntervalEnd == finding.IntervalEnd
        && flag.MissingIntervals == finding.MissingIntervals
        && flag.Sequence == finding.Sequence
        && flag.ObservedKwh == finding.ObservedKwh
        && flag.BaselineKwh == finding.BaselineKwh
        && flag.RuleVersion == RuleVersion;

    /// <summary>
    /// Where flags must be recomputed after readings were added between
    /// <paramref name="firstNewStart"/> and <paramref name="lastNewStart"/>:
    /// from the stored reading just before them (its gap may now be split) to
    /// the end of the spike look-back after them (later baselines changed).
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) AffectedRange(
        DateTimeOffset firstNewStart, DateTimeOffset lastNewStart, DateTimeOffset? previousStoredStart, int intervalMinutes) =>
        (previousStoredStart ?? firstNewStart, lastNewStart + SpikeLookback + TimeSpan.FromMinutes(intervalMinutes));
}
