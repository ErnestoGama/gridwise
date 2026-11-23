using System.Globalization;
using GridWise.Api.Data;
using GridWise.Api.Http;
using GridWise.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace GridWise.Api.Telemetry;

public sealed record QualityFlagView(
    Guid Id, string Kind, DateTimeOffset IntervalStart, DateTimeOffset IntervalEnd, int? MissingIntervals,
    long? Sequence, decimal? ObservedKwh, decimal? BaselineKwh, int RuleVersion,
    DateTimeOffset FlaggedAt, DateTimeOffset UpdatedAt);

/// <summary>How many flags one refresh added, changed and removed.</summary>
public sealed record QualityFlagChanges(int Raised, int Updated, int Cleared)
{
    public static readonly QualityFlagChanges None = new(0, 0, 0);

    public QualityFlagChanges Add(QualityFlagChanges other) =>
        new(Raised + other.Raised, Updated + other.Updated, Cleared + other.Cleared);
}

/// <summary>
/// Keeps <c>reading_quality_flags</c> in step with the stored readings and
/// lists them per meter. Every member can read flags: operators need them to
/// know which numbers are unknown or suspicious.
/// </summary>
public static class QualityFlagEndpoints
{
    public static IEndpointRouteBuilder MapQualityFlagEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/v1/organizations/{organizationId:guid}/meters/{meterId:guid}/quality-flags", ListFlagsAsync)
            .RequireAuthorization();
        return routes;
    }

    /// <summary>
    /// Recomputes flags around newly stored readings. Runs inside the ingestion
    /// transaction, under the per-meter locks, after the readings were saved,
    /// so concurrent batches for a meter cannot compute flags from different
    /// views of its readings. Changes are tracked, not saved.
    /// </summary>
    public static async Task<QualityFlagChanges> RefreshAsync(
        GridWiseDbContext db, Guid organizationId, IReadOnlyList<ValidReading> inserted,
        IReadOnlyDictionary<Guid, int> meterIntervals, CancellationToken ct)
    {
        var changes = QualityFlagChanges.None;
        foreach (var meter in inserted.GroupBy(r => r.MeterId).OrderBy(g => g.Key))
        {
            var meterId = meter.Key;
            var interval = meterIntervals[meterId];
            var first = meter.Min(r => r.ReadingStart);
            var last = meter.Max(r => r.ReadingStart);

            var readings = db.MeterReadings.Where(r => r.OrganizationId == organizationId && r.MeterId == meterId);
            var previous = await readings
                .Where(r => r.ReadingStart < first)
                .OrderByDescending(r => r.ReadingStart)
                .Select(r => (DateTimeOffset?)r.ReadingStart)
                .FirstOrDefaultAsync(ct);
            var (from, to) = ReadingQuality.AffectedRange(first, last, previous, interval);

            var contextStart = from - ReadingQuality.SpikeLookback;
            var window = await readings
                .Where(r => r.ReadingStart >= contextStart && r.ReadingStart < to)
                .Select(r => new StoredReading(r.MeterId, r.Sequence, r.ReadingStart, r.EnergyKwh))
                .ToListAsync(ct);
            // The first reading after the range closes a gap that crosses its end.
            var after = await readings
                .Where(r => r.ReadingStart >= to)
                .OrderBy(r => r.ReadingStart)
                .Select(r => new StoredReading(r.MeterId, r.Sequence, r.ReadingStart, r.EnergyKwh))
                .FirstOrDefaultAsync(ct);
            if (after is not null)
            {
                window.Add(after);
            }

            var findings = ReadingQuality.Evaluate(window, interval, from, to);
            var stored = await db.ReadingQualityFlags
                .Where(f => f.OrganizationId == organizationId && f.MeterId == meterId
                    && f.IntervalStart >= from && f.IntervalStart < to)
                .ToListAsync(ct);
            changes = changes.Add(Apply(db, organizationId, meterId, stored, findings));
        }
        return changes;
    }

    /// <summary>
    /// Brings the stored flags of one range to the computed findings: unchanged
    /// flags keep their timestamps, changed ones are updated in place, stale
    /// ones are removed (e.g. a gap filled by a late reading).
    /// </summary>
    public static QualityFlagChanges Apply(
        GridWiseDbContext db, Guid organizationId, Guid meterId,
        IReadOnlyList<ReadingQualityFlag> stored, IReadOnlyList<QualityFinding> findings)
    {
        var existing = stored.ToDictionary(f => (f.Kind, f.IntervalStart.UtcTicks));
        int raised = 0, updated = 0;
        foreach (var finding in findings)
        {
            if (existing.Remove((finding.Kind, finding.IntervalStart.UtcTicks), out var flag))
            {
                if (!ReadingQuality.Matches(flag, finding))
                {
                    Copy(finding, flag);
                    updated++;
                }
                continue;
            }

            var created = new ReadingQualityFlag { OrganizationId = organizationId, MeterId = meterId, Kind = finding.Kind };
            Copy(finding, created);
            db.ReadingQualityFlags.Add(created);
            raised++;
        }
        db.ReadingQualityFlags.RemoveRange(existing.Values);
        return new QualityFlagChanges(raised, updated, existing.Count);
    }

    private static void Copy(QualityFinding finding, ReadingQualityFlag flag)
    {
        flag.IntervalStart = finding.IntervalStart;
        flag.IntervalEnd = finding.IntervalEnd;
        flag.MissingIntervals = finding.MissingIntervals;
        flag.Sequence = finding.Sequence;
        flag.ObservedKwh = finding.ObservedKwh;
        flag.BaselineKwh = finding.BaselineKwh;
        flag.RuleVersion = ReadingQuality.RuleVersion;
    }

    /// <summary>
    /// Flags of one meter in time order. <c>from</c>/<c>to</c> select flags
    /// that overlap the window; <c>kind</c> narrows to gaps or spikes.
    /// </summary>
    private static async Task<IResult> ListFlagsAsync(
        Guid organizationId, Guid meterId, string? kind, string? from, string? to, int? limit, string? cursor,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Operator, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var errors = new Dictionary<string, string[]>();
        var (size, after) = Paging.Read(limit, cursor, errors);
        if (kind is not null && !ReadingQuality.Kinds.Contains(kind))
        {
            errors["kind"] = [$"Kind must be one of: {string.Join(", ", ReadingQuality.Kinds)}."];
        }
        var fromUtc = Instants.Read(from, "from", errors);
        var toUtc = Instants.Read(to, "to", errors);
        if (fromUtc is not null && toUtc is not null && toUtc <= fromUtc)
        {
            errors["to"] = ["To must be after from."];
        }
        (DateTimeOffset Start, string Kind)? afterKey = null;
        if (after is not null)
        {
            afterKey = ReadCursor(after);
            if (afterKey is null)
            {
                errors["cursor"] = ["Cursor is not valid."];
            }
        }
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(http, errors);
        }

        var meterExists = await db.Meters.AnyAsync(m => m.OrganizationId == organizationId && m.Id == meterId, ct);
        if (!meterExists)
        {
            return ApiErrors.NotFound(http, "Meter not found.");
        }

        var query = db.ReadingQualityFlags.Where(f => f.OrganizationId == organizationId && f.MeterId == meterId);
        if (kind is not null)
        {
            query = query.Where(f => f.Kind == kind);
        }
        if (fromUtc is { } windowStart)
        {
            query = query.Where(f => f.IntervalEnd > windowStart);
        }
        if (toUtc is { } windowEnd)
        {
            query = query.Where(f => f.IntervalStart < windowEnd);
        }
        if (afterKey is { } key)
        {
            query = query.Where(f => f.IntervalStart > key.Start
                || (f.IntervalStart == key.Start && string.Compare(f.Kind, key.Kind) > 0));
        }

        var rows = await query
            .OrderBy(f => f.IntervalStart).ThenBy(f => f.Kind)
            .Take(size + 1)
            .Select(f => new QualityFlagView(
                f.Id, f.Kind, f.IntervalStart, f.IntervalEnd, f.MissingIntervals, f.Sequence, f.ObservedKwh,
                f.BaselineKwh, f.RuleVersion, f.CreatedAt, f.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(Paging.Build(rows, size,
            f => $"{f.IntervalStart.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{f.Kind}"));
    }

    private static (DateTimeOffset Start, string Kind)? ReadCursor(string value)
    {
        var parts = value.Split(':');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && ReadingQuality.Kinds.Contains(parts[1]))
        {
            return (new DateTimeOffset(ticks, TimeSpan.Zero), parts[1]);
        }
        return null;
    }
}
