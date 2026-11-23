using GridWise.Api.Data;
using GridWise.Api.Http;
using GridWise.Api.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GridWise.Api.Telemetry;

public sealed record TelemetryBatchSummary(
    Guid Id, int Received, int Inserted, int Duplicates, int Conflicts, bool HasIdempotencyKey, DateTimeOffset ReceivedAt);

public sealed record TelemetryBatchDetail(
    Guid Id, int Received, int Inserted, int Duplicates, bool HasIdempotencyKey, Guid? SubmittedByUserId,
    DateTimeOffset ReceivedAt, IReadOnlyList<ReadingConflict> Conflicts);

/// <summary>
/// Telemetry ingestion and its history. A batch may hold readings for several
/// meters of the organization in the route. Resending a batch is safe:
/// identical readings are counted as duplicates and nothing is stored twice;
/// with an <c>Idempotency-Key</c> the original result is returned as is.
/// New readings refresh the meter's gap and spike flags in the same transaction.
/// </summary>
public static class TelemetryEndpoints
{
    public const string ReplayedHeader = "Idempotent-Replayed";

    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/organizations/{organizationId:guid}/telemetry").RequireAuthorization();
        group.MapPost("/batches", IngestBatchAsync)
            .WithMetadata(new RequestSizeLimitAttribute(TelemetryIngestion.MaxBodyBytes));
        group.MapGet("/batches", ListBatchesAsync);
        group.MapGet("/batches/{batchId:guid}", GetBatchAsync);

        return routes;
    }

    private static async Task<IResult> IngestBatchAsync(
        Guid organizationId, TelemetryBatchRequest request,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, TimeProvider clock,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var (decision, caller) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Admin, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        string? idempotencyKey = null;
        if (http.Request.Headers.TryGetValue(IdempotencyKeys.HeaderName, out var keyValues))
        {
            idempotencyKey = keyValues.Count == 1 ? keyValues[0] : null;
            if (!IdempotencyKeys.IsValid(idempotencyKey))
            {
                return ApiErrors.Validation(http, new Dictionary<string, string[]>
                {
                    ["idempotencyKey"] = [$"Idempotency-Key must be 1 to {IdempotencyKeys.MaxLength} letters, digits or . _ : -"],
                });
            }
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

        var fingerprint = TelemetryIngestion.Fingerprint(readings);
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
            // Key lock first, then meter locks in ID order, so two batches never wait on each other in a cycle.
            if (idempotencyKey is not null)
            {
                var keyLock = $"{organizationId}/{idempotencyKey}";
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({keyLock}, 0))", ct);
            }
            foreach (var meterId in meterIds)
            {
                await db.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({meterId.ToString()}, 0))", ct);
            }
        }

        if (idempotencyKey is not null)
        {
            var earlier = await db.TelemetryBatches
                .Where(b => b.OrganizationId == organizationId && b.IdempotencyKey == idempotencyKey)
                .Select(b => new { b.Id, b.PayloadSha256 })
                .SingleOrDefaultAsync(ct);
            if (earlier is not null)
            {
                if (earlier.PayloadSha256 != fingerprint)
                {
                    return IdempotencyKeyReused(http);
                }
                http.Response.Headers[ReplayedHeader] = "true";
                return Results.Ok(await LoadResultAsync(db, earlier.Id, ct));
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

        var batch = new TelemetryBatch
        {
            OrganizationId = organizationId,
            SubmittedByUserId = caller!.UserId,
            IdempotencyKey = idempotencyKey,
            PayloadSha256 = fingerprint,
            ReceivedCount = readings.Count,
            InsertedCount = plan.Inserts.Count,
            DuplicateCount = plan.Duplicates,
            ConflictCount = plan.Conflicts.Count,
            ReceivedAt = receivedAt,
        };
        batch.Conflicts.AddRange(plan.Conflicts.Select(c => new TelemetryBatchConflict
        {
            ReadingIndex = c.Index,
            MeterId = c.MeterId,
            Sequence = c.Sequence,
            Code = c.Code,
        }));
        db.TelemetryBatches.Add(batch);

        var flags = QualityFlagChanges.None;
        try
        {
            await db.SaveChangesAsync(ct);
            // Flags read the readings just saved, still under the meter locks and in the same transaction.
            if (plan.Inserts.Count > 0)
            {
                flags = await QualityFlagEndpoints.RefreshAsync(db, organizationId, plan.Inserts, meterIntervals, ct);
                await db.SaveChangesAsync(ct);
            }
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Only reachable if a writer bypasses the advisory locks; the batch is rolled back and can be resent.
            return ApiErrors.Conflict(http, "ingestion_conflict", "Readings changed while the batch was stored. Send it again.");
        }

        var result = new TelemetryBatchResult(
            batch.Id, readings.Count, plan.Inserts.Count, plan.Duplicates, plan.Conflicts, receivedAt);
        // Counts only: reading values, meter serials and idempotency keys are not logged.
        loggers.CreateLogger("GridWise.Telemetry").LogInformation(
            "Telemetry batch {BatchId} for organization {OrganizationId}: {Received} received, {Inserted} inserted, {Duplicates} duplicates, {Conflicts} conflicts; quality flags {FlagsRaised} raised, {FlagsUpdated} updated, {FlagsCleared} cleared",
            batch.Id, organizationId, result.Received, result.Inserted, result.Duplicates, result.Conflicts.Count,
            flags.Raised, flags.Updated, flags.Cleared);
        return Results.Ok(result);
    }

    /// <summary>Batch history for the organization, newest first. Analysts and above can review it.</summary>
    private static async Task<IResult> ListBatchesAsync(
        Guid organizationId, int? limit, string? cursor,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Analyst, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var errors = new Dictionary<string, string[]>();
        var (size, after) = Paging.Read(limit, cursor, errors);
        Guid? afterId = null;
        if (after is not null)
        {
            if (Guid.TryParseExact(after, "D", out var parsed))
            {
                afterId = parsed;
            }
            else
            {
                errors["cursor"] = ["Cursor is not valid."];
            }
        }
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(http, errors);
        }

        var query = db.TelemetryBatches.Where(b => b.OrganizationId == organizationId);
        if (afterId is { } id)
        {
            query = query.Where(b => b.Id.CompareTo(id) < 0);
        }
        var rows = await query
            .OrderByDescending(b => b.Id)
            .Take(size + 1)
            .Select(b => new TelemetryBatchSummary(
                b.Id, b.ReceivedCount, b.InsertedCount, b.DuplicateCount, b.ConflictCount,
                b.IdempotencyKey != null, b.ReceivedAt))
            .ToListAsync(ct);

        return Results.Ok(Paging.Build(rows, size, b => b.Id.ToString("D")));
    }

    private static async Task<IResult> GetBatchAsync(
        Guid organizationId, Guid batchId,
        HttpContext http, OrganizationAccess access, GridWiseDbContext db, CancellationToken ct)
    {
        var (decision, _) = await access.CheckAsync(http.User, organizationId, OrganizationRole.Analyst, ct);
        if (decision != AccessDecision.Granted)
        {
            return decision.ToError(http);
        }

        var batch = await db.TelemetryBatches
            .Where(b => b.OrganizationId == organizationId && b.Id == batchId)
            .Select(b => new
            {
                b.Id, b.ReceivedCount, b.InsertedCount, b.DuplicateCount, b.IdempotencyKey, b.SubmittedByUserId, b.ReceivedAt,
            })
            .SingleOrDefaultAsync(ct);
        if (batch is null)
        {
            return ApiErrors.NotFound(http, "Telemetry batch not found.");
        }

        var conflicts = await LoadConflictsAsync(db, batch.Id, ct);
        return Results.Ok(new TelemetryBatchDetail(
            batch.Id, batch.ReceivedCount, batch.InsertedCount, batch.DuplicateCount, batch.IdempotencyKey != null,
            batch.SubmittedByUserId, batch.ReceivedAt, conflicts));
    }

    /// <summary>The stored result of a batch, in the same shape the original request returned.</summary>
    private static async Task<TelemetryBatchResult> LoadResultAsync(GridWiseDbContext db, Guid batchId, CancellationToken ct)
    {
        var batch = await db.TelemetryBatches.SingleAsync(b => b.Id == batchId, ct);
        var conflicts = await LoadConflictsAsync(db, batchId, ct);
        return new TelemetryBatchResult(
            batch.Id, batch.ReceivedCount, batch.InsertedCount, batch.DuplicateCount, conflicts, batch.ReceivedAt);
    }

    private static async Task<IReadOnlyList<ReadingConflict>> LoadConflictsAsync(GridWiseDbContext db, Guid batchId, CancellationToken ct)
    {
        var rows = await db.TelemetryBatchConflicts
            .Where(c => c.BatchId == batchId)
            .OrderBy(c => c.ReadingIndex)
            .Select(c => new { c.ReadingIndex, c.MeterId, c.Sequence, c.Code })
            .ToListAsync(ct);
        return rows
            .Select(c => new ReadingConflict(c.ReadingIndex, c.MeterId, c.Sequence, c.Code, TelemetryIngestion.ConflictMessage(c.Code)))
            .ToList();
    }

    private static IResult IdempotencyKeyReused(HttpContext http) =>
        ApiErrors.Conflict(http, "idempotency_key_reused",
            "This Idempotency-Key was already used for a different batch. Use a new key for new readings.");
}
