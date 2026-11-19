using GridWise.Api.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridWise.Api.Data;

internal sealed class BuildingConfiguration : IEntityTypeConfiguration<Building>
{
    public void Configure(EntityTypeBuilder<Building> b)
    {
        b.ToTable("buildings", t =>
        {
            t.HasCheckConstraint("ck_buildings_name", "length(btrim(name)) > 0");
            t.HasCheckConstraint("ck_buildings_floor_area_m2",
                $"floor_area_m2 IS NULL OR (floor_area_m2 > 0 AND floor_area_m2 <= {Building.MaxFloorAreaM2})");
        });
        b.HasKey(x => x.Id).HasName("pk_buildings");
        // Target of the meters' composite foreign key: a meter can only point at a building of its own organization.
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_buildings_organization_id_id");
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.OrganizationId).HasColumnName("organization_id");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(Building.NameMaxLength).IsRequired();
        b.Property(x => x.TimeZone).HasColumnName("time_zone").HasMaxLength(Building.TimeZoneMaxLength).IsRequired();
        b.Property(x => x.FloorAreaM2).HasColumnName("floor_area_m2");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasDatabaseName("ux_buildings_organization_name");

        // Restrict: removing an organization must not silently delete its buildings and their history.
        b.HasOne(x => x.Organization)
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .HasConstraintName("fk_buildings_organizations_organization_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MeterConfiguration : IEntityTypeConfiguration<Meter>
{
    public void Configure(EntityTypeBuilder<Meter> b)
    {
        var kinds = string.Join(", ", MeterRules.Kinds.Select(k => $"'{k}'"));
        var intervals = string.Join(", ", MeterRules.IntervalMinutes);
        b.ToTable("meters", t =>
        {
            t.HasCheckConstraint("ck_meters_kind", $"kind IN ({kinds})");
            t.HasCheckConstraint("ck_meters_interval_minutes", $"interval_minutes IN ({intervals})");
            t.HasCheckConstraint("ck_meters_serial", "serial ~ '^[A-Z0-9][A-Z0-9-]{2,39}$'");
        });
        b.HasKey(x => x.Id).HasName("pk_meters");
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.OrganizationId).HasColumnName("organization_id");
        b.Property(x => x.BuildingId).HasColumnName("building_id");
        b.Property(x => x.Serial).HasColumnName("serial").HasMaxLength(Meter.SerialMaxLength).IsRequired();
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
        b.Property(x => x.IntervalMinutes).HasColumnName("interval_minutes");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(x => new { x.OrganizationId, x.Serial }).IsUnique().HasDatabaseName("ux_meters_organization_serial");
        b.HasIndex(x => new { x.OrganizationId, x.BuildingId }).HasDatabaseName("ix_meters_organization_building");

        b.HasOne(x => x.Building)
            .WithMany(x => x.Meters)
            .HasForeignKey(x => new { x.OrganizationId, x.BuildingId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .HasConstraintName("fk_meters_buildings_organization_id_building_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
