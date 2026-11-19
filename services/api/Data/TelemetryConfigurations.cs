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
