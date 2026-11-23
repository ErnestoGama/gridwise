using GridWise.Api.Data;
using GridWise.Api.Http;
using GridWise.Api.Identity;
using GridWise.Api.Realtime;
using Microsoft.EntityFrameworkCore;

namespace GridWise.Api.Telemetry;

/// <summary>
/// Stored readings and quality flags of one meter inside the requested window.
/// Readings use the same shape as the live <c>ReadingsIngested</c> message, so
/// the dashboard can merge both without converting.
/// </summary>
public sealed record MeterWindowView(
    Guid MeterId, string Serial, string Kind, int IntervalMinutes,
    IReadOnlyList<LiveReading> Readings, IReadOnlyList<QualityFlagView> QualityFlags);

public sealed record BuildingReadingsView(
    Guid BuildingId, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<MeterWindowView> Meters);

/// <summary>
/// Initial load for the live dashboard: every meter of a building with its
/// readings and flags in a bounded window. Missing intervals are left out, never
/// filled with zero; the gap flags say which intervals are unknown.
/// </summary>
public static class ReadingEndpoints
{
    /// <summary>Two days of 5-minute readings is 576 per meter, small enough for one response.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(48);

    public static IEndpointRouteBuilder MapReadingEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/v1/organizations/{organizationId:guid}/buildings/{buildingId:guid}/readings", GetReadingsAsync)
            .RequireAuthorization();
        return routes;
    }

    private static async Task<IResult> GetReadingsAsync(
        Guid organizationId, Guid buildingId, string? from, string? to,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        // Every member can read, the same as buildings, flags and the live hub.
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Operator, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var errors = new Dictionary<string, string[]>();
        var fromUtc = Instants.Read(from, "from", errors);
        var toUtc = Instants.Read(to, "to", errors);
        if (string.IsNullOrEmpty(from))
        {
            errors["from"] = ["From is required."];
        }
        if (string.IsNullOrEmpty(to))
        {
            errors["to"] = ["To is required."];
        }
        if (fromUtc is { } start && toUtc is { } end)
        {
            if (end <= start)
            {
                errors["to"] = ["To must be after from."];
            }
            else if (end - start > MaxWindow)
            {
                errors["to"] = [$"The window can be at most {MaxWindow.TotalHours:0} hours."];
            }
        }
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(http, errors);
        }
        var windowStart = fromUtc!.Value;
        var windowEnd = toUtc!.Value;

        var buildingExists = await db.Buildings.AnyAsync(b => b.OrganizationId == organizationId && b.Id == buildingId, ct);
        if (!buildingExists)
        {
            return ApiErrors.NotFound(http, "Building not found.");
        }

        var meters = await db.Meters
            .Where(m => m.OrganizationId == organizationId && m.BuildingId == buildingId)
            .OrderBy(m => m.Serial)
            .Select(m => new { m.Id, m.Serial, m.Kind, m.IntervalMinutes })
            .ToListAsync(ct);
        var meterIds = meters.Select(m => m.Id).ToList();

        var readings = await db.MeterReadings
            .Where(r => r.OrganizationId == organizationId && meterIds.Contains(r.MeterId)
                && r.ReadingStart >= windowStart && r.ReadingStart < windowEnd)
            .OrderBy(r => r.MeterId).ThenBy(r => r.ReadingStart)
            .Select(r => new { r.MeterId, Reading = new LiveReading(r.Sequence, r.ReadingStart, r.EnergyKwh) })
            .ToListAsync(ct);
        var flags = await db.ReadingQualityFlags
            .Where(f => f.OrganizationId == organizationId && meterIds.Contains(f.MeterId)
                && f.IntervalEnd > windowStart && f.IntervalStart < windowEnd)
            .OrderBy(f => f.IntervalStart).ThenBy(f => f.Kind)
            .Select(f => new
            {
                f.MeterId,
                View = new QualityFlagView(
                    f.Id, f.Kind, f.IntervalStart, f.IntervalEnd, f.MissingIntervals, f.Sequence, f.ObservedKwh,
                    f.BaselineKwh, f.RuleVersion, f.CreatedAt, f.UpdatedAt),
            })
            .ToListAsync(ct);

        var readingsByMeter = readings.ToLookup(r => r.MeterId, r => r.Reading);
        var flagsByMeter = flags.ToLookup(f => f.MeterId, f => f.View);
        var views = meters
            .Select(m => new MeterWindowView(
                m.Id, m.Serial, m.Kind, m.IntervalMinutes, readingsByMeter[m.Id].ToList(), flagsByMeter[m.Id].ToList()))
            .ToList();

        return Results.Ok(new BuildingReadingsView(buildingId, windowStart, windowEnd, views));
    }
}
