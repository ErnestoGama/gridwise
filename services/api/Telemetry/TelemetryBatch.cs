using System.Text.RegularExpressions;
using GridWise.Api.Identity;

namespace GridWise.Api.Telemetry;

/// <summary>
/// The outcome of one accepted telemetry batch: how many readings were new,
/// how many were identical resends and which ones conflicted with stored
/// readings. Batches are written once and never changed, so operators can see
/// afterwards what deduplication did with each delivery.
/// </summary>
public sealed class TelemetryBatch
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; set; }

    /// <summary>The user who sent the batch; null if that user was later removed.</summary>
    public Guid? SubmittedByUserId { get; set; }

    /// <summary>Optional client key; a retry with the same key and body returns this batch instead of ingesting again.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>SHA-256 (hex) of the normalized readings, see <see cref="TelemetryIngestion.Fingerprint"/>.</summary>
    public required string PayloadSha256 { get; set; }

    public int ReceivedCount { get; set; }
    public int InsertedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ConflictCount { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public List<TelemetryBatchConflict> Conflicts { get; } = [];
}

/// <summary>A reading of a batch that was not stored because it contradicted an existing reading.</summary>
public sealed class TelemetryBatchConflict
{
    public Guid BatchId { get; set; }

    /// <summary>Position of the reading in the submitted batch.</summary>
    public int ReadingIndex { get; set; }

    public Guid MeterId { get; set; }
    public long Sequence { get; set; }

    /// <summary>One of <see cref="TelemetryIngestion.ConflictCodes"/>.</summary>
    public required string Code { get; set; }

    public TelemetryBatch Batch { get; set; } = null!;
}

public static partial class IdempotencyKeys
{
    public const string HeaderName = "Idempotency-Key";
    public const int MaxLength = 64;

    /// <summary>1–64 characters of letters, digits and <c>. _ : -</c>; same rule as <c>ck_telemetry_batches_idempotency_key</c>.</summary>
    public static bool IsValid(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxLength && Pattern().IsMatch(key);

    [GeneratedRegex(@"^[A-Za-z0-9._:-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
