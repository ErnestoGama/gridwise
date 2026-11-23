using GridWise.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridWise.Api.Migrations;

/// <summary>
/// Adds <c>reading_quality_flags</c>: gaps and spikes found in stored
/// readings. Flags reference the meter (same organization through the
/// composite key) but not individual readings, so <c>meter_readings</c> stays
/// an append-only hypertable. Rollback: <c>Down</c> drops the table; flags are
/// derived data and can be recomputed from the readings.
/// </summary>
[DbContext(typeof(GridWiseDbContext))]
[Migration("20261123140000_AddReadingQualityFlags")]
public partial class AddReadingQualityFlags : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "reading_quality_flags",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                meter_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                interval_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                interval_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                missing_intervals = table.Column<int>(type: "integer", nullable: true),
                sequence = table.Column<long>(type: "bigint", nullable: true),
                observed_kwh = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                baseline_kwh = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                rule_version = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_reading_quality_flags", x => x.id);
                table.CheckConstraint("ck_reading_quality_flags_kind", "kind IN ('gap', 'spike')");
                table.CheckConstraint("ck_reading_quality_flags_interval", "interval_end > interval_start");
                table.CheckConstraint("ck_reading_quality_flags_shape",
                    "(kind = 'gap' AND missing_intervals IS NOT NULL AND missing_intervals > 0 AND sequence IS NULL AND observed_kwh IS NULL AND baseline_kwh IS NULL) OR "
                    + "(kind = 'spike' AND missing_intervals IS NULL AND sequence IS NOT NULL AND sequence >= 0 "
                    + "AND observed_kwh IS NOT NULL AND observed_kwh >= 0 AND baseline_kwh IS NOT NULL AND baseline_kwh >= 0)");
                table.CheckConstraint("ck_reading_quality_flags_rule_version", "rule_version > 0");
                table.ForeignKey(
                    name: "fk_reading_quality_flags_meters_organization_id_meter_id",
                    columns: x => new { x.organization_id, x.meter_id },
                    principalTable: "meters",
                    principalColumns: ["organization_id", "id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ux_reading_quality_flags_meter_start_kind",
            table: "reading_quality_flags",
            columns: ["meter_id", "interval_start", "kind"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_reading_quality_flags_organization_meter_interval_start",
            table: "reading_quality_flags",
            columns: ["organization_id", "meter_id", "interval_start"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "reading_quality_flags");
    }
}
