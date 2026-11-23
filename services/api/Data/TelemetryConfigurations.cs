using GridWise.Api.Identity;
using GridWise.Api.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridWise.Api.Data;

internal sealed class MeterReadingConfiguration : IEntityTypeConfiguration<MeterReading>
{
    public void Configure(EntityTypeBuilder<MeterReading> b)
    {
        b.ToTable("meter_readings", t =>
        {
            t.HasCheckConstraint("ck_meter_readings_energy_kwh", "energy_kwh >= 0");
            t.HasCheckConstraint("ck_meter_readings_sequence", "sequence >= 0");
            t.HasCheckConstraint("ck_meter_readings_reading_start",
                "reading_start = date_trunc('minute', reading_start)");
        });

        // TimescaleDB requires every unique index on a hypertable to include the time column.
        b.HasKey(x => new { x.MeterId, x.ReadingStart }).HasName("pk_meter_readings");
        b.Property(x => x.OrganizationId).HasColumnName("organization_id");
        b.Property(x => x.MeterId).HasColumnName("meter_id");
        b.Property(x => x.ReadingStart).HasColumnName("reading_start");
        b.Property(x => x.Sequence).HasColumnName("sequence");
        b.Property(x => x.EnergyKwh).HasColumnName("energy_kwh").HasPrecision(12, 3);
        b.Property(x => x.ReceivedAt).HasColumnName("received_at").HasDefaultValueSql("now()");

        b.HasIndex(x => new { x.MeterId, x.Sequence }).HasDatabaseName("ix_meter_readings_meter_sequence");
        b.HasIndex(x => new { x.OrganizationId, x.ReadingStart }).HasDatabaseName("ix_meter_readings_organization_reading_start");

        b.HasOne(x => x.Meter)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.MeterId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .HasConstraintName("fk_meter_readings_meters_organization_id_meter_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TelemetryBatchConfiguration : IEntityTypeConfiguration<TelemetryBatch>
{
    public void Configure(EntityTypeBuilder<TelemetryBatch> b)
    {
        b.ToTable("telemetry_batches", t =>
        {
            t.HasCheckConstraint("ck_telemetry_batches_counts",
                "received_count > 0 AND inserted_count >= 0 AND duplicate_count >= 0 AND conflict_count >= 0 "
                + "AND inserted_count + duplicate_count + conflict_count = received_count");
            t.HasCheckConstraint("ck_telemetry_batches_idempotency_key",
                $"idempotency_key IS NULL OR idempotency_key ~ '^[A-Za-z0-9._:-]{{1,{IdempotencyKeys.MaxLength}}}$'");
            t.HasCheckConstraint("ck_telemetry_batches_payload_sha256", "payload_sha256 ~ '^[0-9a-f]{64}$'");
        });
        b.HasKey(x => x.Id).HasName("pk_telemetry_batches");
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.OrganizationId).HasColumnName("organization_id");
        b.Property(x => x.SubmittedByUserId).HasColumnName("submitted_by_user_id");
        b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(IdempotencyKeys.MaxLength);
        b.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64).IsFixedLength().IsRequired();
        b.Property(x => x.ReceivedCount).HasColumnName("received_count");
        b.Property(x => x.InsertedCount).HasColumnName("inserted_count");
        b.Property(x => x.DuplicateCount).HasColumnName("duplicate_count");
        b.Property(x => x.ConflictCount).HasColumnName("conflict_count");
        b.Property(x => x.ReceivedAt).HasColumnName("received_at");

        // One batch per key and organization; batches without a key are not limited.
        b.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey })
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL")
            .HasDatabaseName("ux_telemetry_batches_organization_idempotency_key");
        // Batch history is listed newest first; UUIDv7 IDs sort by creation time.
        b.HasIndex(x => new { x.OrganizationId, x.Id }).HasDatabaseName("ix_telemetry_batches_organization_id");
        b.HasIndex(x => x.SubmittedByUserId).HasDatabaseName("ix_telemetry_batches_submitted_by_user_id");

        b.HasOne(x => x.Organization)
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .HasConstraintName("fk_telemetry_batches_organizations_organization_id")
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(x => x.SubmittedByUserId)
            .HasConstraintName("fk_telemetry_batches_users_submitted_by_user_id")
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class TelemetryBatchConflictConfiguration : IEntityTypeConfiguration<TelemetryBatchConflict>
{
    public void Configure(EntityTypeBuilder<TelemetryBatchConflict> b)
    {
        var codes = string.Join(", ", TelemetryIngestion.ConflictCodes.Select(c => $"'{c}'"));
        b.ToTable("telemetry_batch_conflicts", t =>
        {
            t.HasCheckConstraint("ck_telemetry_batch_conflicts_code", $"code IN ({codes})");
            t.HasCheckConstraint("ck_telemetry_batch_conflicts_reading_index",
                $"reading_index >= 0 AND reading_index < {TelemetryIngestion.MaxReadingsPerBatch}");
        });
        b.HasKey(x => new { x.BatchId, x.ReadingIndex }).HasName("pk_telemetry_batch_conflicts");
        b.Property(x => x.BatchId).HasColumnName("batch_id");
        b.Property(x => x.ReadingIndex).HasColumnName("reading_index");
        b.Property(x => x.MeterId).HasColumnName("meter_id");
        b.Property(x => x.Sequence).HasColumnName("sequence");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(32).IsRequired();

        // No foreign key to meters: the conflict records what was sent, and the
        // meter was already checked to belong to the batch's organization.
        b.HasOne(x => x.Batch)
            .WithMany(x => x.Conflicts)
            .HasForeignKey(x => x.BatchId)
            .HasConstraintName("fk_telemetry_batch_conflicts_telemetry_batches_batch_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReadingQualityFlagConfiguration : IEntityTypeConfiguration<ReadingQualityFlag>
{
    /// <summary>
    /// Gaps carry a count only, spikes the reading and its baseline; never both.
    /// Every spike column is tested with <c>IS NOT NULL</c> because a check that
    /// evaluates to NULL passes.
    /// </summary>
    public const string ShapeCheck =
        "(kind = 'gap' AND missing_intervals IS NOT NULL AND missing_intervals > 0 AND sequence IS NULL AND observed_kwh IS NULL AND baseline_kwh IS NULL) OR "
        + "(kind = 'spike' AND missing_intervals IS NULL AND sequence IS NOT NULL AND sequence >= 0 "
        + "AND observed_kwh IS NOT NULL AND observed_kwh >= 0 AND baseline_kwh IS NOT NULL AND baseline_kwh >= 0)";

    public void Configure(EntityTypeBuilder<ReadingQualityFlag> b)
    {
        var kinds = string.Join(", ", ReadingQuality.Kinds.Select(k => $"'{k}'"));
        b.ToTable("reading_quality_flags", t =>
        {
            t.HasCheckConstraint("ck_reading_quality_flags_kind", $"kind IN ({kinds})");
            t.HasCheckConstraint("ck_reading_quality_flags_interval", "interval_end > interval_start");
            t.HasCheckConstraint("ck_reading_quality_flags_shape", ShapeCheck);
            t.HasCheckConstraint("ck_reading_quality_flags_rule_version", "rule_version > 0");
        });
        b.HasKey(x => x.Id).HasName("pk_reading_quality_flags");
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.OrganizationId).HasColumnName("organization_id");
        b.Property(x => x.MeterId).HasColumnName("meter_id");
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
        b.Property(x => x.IntervalStart).HasColumnName("interval_start");
        b.Property(x => x.IntervalEnd).HasColumnName("interval_end");
        b.Property(x => x.MissingIntervals).HasColumnName("missing_intervals");
        b.Property(x => x.Sequence).HasColumnName("sequence");
        b.Property(x => x.ObservedKwh).HasColumnName("observed_kwh").HasPrecision(12, 3);
        b.Property(x => x.BaselineKwh).HasColumnName("baseline_kwh").HasPrecision(12, 3);
        b.Property(x => x.RuleVersion).HasColumnName("rule_version");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        // One flag of a kind per meter and start.
        b.HasIndex(x => new { x.MeterId, x.IntervalStart, x.Kind })
            .IsUnique()
            .HasDatabaseName("ux_reading_quality_flags_meter_start_kind");
        // Tenant-scoped listing of a meter's flags; also serves the composite foreign key.
        b.HasIndex(x => new { x.OrganizationId, x.MeterId, x.IntervalStart })
            .HasDatabaseName("ix_reading_quality_flags_organization_meter_interval_start");

        b.HasOne(x => x.Meter)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.MeterId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .HasConstraintName("fk_reading_quality_flags_meters_organization_id_meter_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
