using GridWise.Api.Data;
using GridWise.Api.Http;
using GridWise.Api.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GridWise.Api.Telemetry;

/// <summary>
/// Telemetry ingestion. A batch may hold readings for several meters of the
/// organization in the route. Resending a batch is safe: identical readings are
/// counted as duplicates and nothing is stored twice.
/// </summary>
public static class TelemetryEndpoints
{
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGroup("/v1/organizations/{organizationId:guid}/telemetry")
            .RequireAuthorization()
            .MapPost("/batches", IngestBatchAsync)
            .WithMetadata(new RequestSizeLimitAttribute(TelemetryIngestion.MaxBodyBytes));

        return routes;
    }

    private static async Task<IResult> IngestBatchAsync(
        Guid organizationId, TelemetryBatchRequest request,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, TimeProvider clock,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Admin, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var requestedMeters = (request.Readings ?? Array.Empty<ReadingInput?>())
            .Take(TelemetryIngestion.MaxReadingsPerBatch)
            .Select(r => r?.MeterId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var meterIntervals = await db.Meters
            .Where(m => m.OrganizationId == organizationId && requestedMeters.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.IntervalMinutes, ct);

        var (errors, readings) = TelemetryIngestion.Validate(request, meterIntervals, clock.GetUtcNow());
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(http, errors);
        }

        var meterIds = readings.Select(r => r.MeterId).Distinct().OrderBy(id => id).ToList();
        var sequences = readings.Select(r => r.Sequence).Distinct().ToList();
        var starts = readings.Select(r => r.ReadingStart).Distinct().ToList();

        // Hypertable unique indexes must include the time column, so (meter, sequence)
        // cannot be a constraint. A per-meter advisory lock serializes concurrent
        // batches for the same meter while the duplicate check and insert run.
        var relational = db.Database.IsRelational();
        await using var transaction = relational ? await db.Database.BeginTransactionAsync(ct) : null;
        if (relational)
        {
            foreach (var meterId in meterIds)
            {
                await db.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({meterId.ToString()}, 0))", ct);
            }
        }

        var existing = await db.MeterReadings
            .Where(r => r.OrganizationId == organizationId
                && meterIds.Contains(r.MeterId)
                && (sequences.Contains(r.Sequence) || starts.Contains(r.ReadingStart)))
            .Select(r => new StoredReading(r.MeterId, r.Sequence, r.ReadingStart, r.EnergyKwh))
            .ToListAsync(ct);

        var plan = TelemetryIngestion.Plan(readings, existing);
        var receivedAt = clock.GetUtcNow();
        db.MeterReadings.AddRange(plan.Inserts.Select(r => new MeterReading
        {
            OrganizationId = organizationId,
            MeterId = r.MeterId,
            ReadingStart = r.ReadingStart,
            Sequence = r.Sequence,
            EnergyKwh = r.EnergyKwh,
            ReceivedAt = receivedAt,
        }));

        try
        {
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Only reachable if a writer bypasses the advisory lock; the batch is rolled back and can be resent.
            return ApiErrors.Conflict(http, "ingestion_conflict", "Readings changed while the batch was stored. Send it again.");
        }

        var result = new TelemetryBatchResult(readings.Count, plan.Inserts.Count, plan.Duplicates, plan.Conflicts);
        // Counts only: reading values and meter serials are not logged.
        loggers.CreateLogger("GridWise.Telemetry").LogInformation(
            "Telemetry batch for organization {OrganizationId}: {Received} received, {Inserted} inserted, {Duplicates} duplicates, {Conflicts} conflicts",
            organizationId, result.Received, result.Inserted, result.Duplicates, result.Conflicts.Count);
        return Results.Ok(result);
    }
}
