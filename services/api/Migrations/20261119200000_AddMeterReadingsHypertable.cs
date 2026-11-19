using GridWise.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridWise.Api.Migrations;

/// <summary>
/// Meter readings as a TimescaleDB hypertable partitioned on <c>reading_start</c>
/// in 7-day chunks. Readings reference meters through
/// <c>(organization_id, meter_id)</c> so a reading can never belong to another
/// tenant's meter, and a trigger rejects updates because readings are immutable.
/// Rollback: <c>Down</c> drops the table and every stored reading with it; take a
/// dump of <c>meter_readings</c> first if the data matters.
/// </summary>
[DbContext(typeof(GridWiseDbContext))]
[Migration("20261119200000_AddMeterReadingsHypertable")]
public partial class AddMeterReadingsHypertable : Migration
{
    internal const string ChunkInterval = "7 days";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Normally created by the Compose init script; repeated so other databases fail early and clearly.
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS timescaledb;");

        migrationBuilder.AddUniqueConstraint(
            name: "ak_meters_organization_id_id",
            table: "meters",
            columns: ["organization_id", "id"]);

        migrationBuilder.CreateTable(
            name: "meter_readings",
            columns: table => new
            {
                meter_id = table.Column<Guid>(type: "uuid", nullable: false),
                reading_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                sequence = table.Column<long>(type: "bigint", nullable: false),
                energy_kwh = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false,
                    defaultValueSql: "now()"),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_meter_readings", x => new { x.meter_id, x.reading_start });
                table.CheckConstraint("ck_meter_readings_energy_kwh", "energy_kwh >= 0");
                table.CheckConstraint("ck_meter_readings_sequence", "sequence >= 0");
                table.CheckConstraint("ck_meter_readings_reading_start",
                    "reading_start = date_trunc('minute', reading_start)");
                table.ForeignKey(
                    name: "fk_meter_readings_meters_organization_id_meter_id",
                    columns: x => new { x.organization_id, x.meter_id },
                    principalTable: "meters",
                    principalColumns: ["organization_id", "id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_meter_readings_meter_sequence",
            table: "meter_readings",
            columns: ["meter_id", "sequence"]);

        migrationBuilder.CreateIndex(
            name: "ix_meter_readings_organization_reading_start",
            table: "meter_readings",
            columns: ["organization_id", "reading_start"]);

        // The table is still empty, so no data migration is needed. Timescale also adds
        // its default reading_start DESC index for time-range scans across meters.
        migrationBuilder.Sql(
            $"SELECT create_hypertable('meter_readings', by_range('reading_start', INTERVAL '{ChunkInterval}'));");

        migrationBuilder.Sql("""
            CREATE FUNCTION meter_readings_reject_update() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'meter readings are immutable'
                    USING ERRCODE = 'restrict_violation';
            END;
            $$;
            """);

        migrationBuilder.Sql("""
            CREATE TRIGGER trg_meter_readings_reject_update
                BEFORE UPDATE ON meter_readings
                FOR EACH ROW EXECUTE FUNCTION meter_readings_reject_update();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Dropping the hypertable also drops its chunks and the trigger.
        migrationBuilder.DropTable(name: "meter_readings");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS meter_readings_reject_update();");
        migrationBuilder.DropUniqueConstraint(name: "ak_meters_organization_id_id", table: "meters");
    }
}
