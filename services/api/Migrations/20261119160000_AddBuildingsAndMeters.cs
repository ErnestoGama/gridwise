using GridWise.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridWise.Api.Migrations;

/// <summary>
/// Buildings and meters, both scoped to an organization. Meters reference
/// buildings through (organization_id, building_id) so a meter can never sit in
/// another tenant's building. Rollback: <c>Down</c> drops both tables; it is
/// only safe before telemetry references meters.
/// </summary>
[DbContext(typeof(GridWiseDbContext))]
[Migration("20261119160000_AddBuildingsAndMeters")]
public partial class AddBuildingsAndMeters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "buildings",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                floor_area_m2 = table.Column<int>(type: "integer", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_buildings", x => x.id);
                table.UniqueConstraint("ak_buildings_organization_id_id", x => new { x.organization_id, x.id });
                table.CheckConstraint("ck_buildings_name", "length(btrim(name)) > 0");
                table.CheckConstraint("ck_buildings_floor_area_m2",
                    "floor_area_m2 IS NULL OR (floor_area_m2 > 0 AND floor_area_m2 <= 2000000)");
                table.ForeignKey(
                    name: "fk_buildings_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "meters",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                building_id = table.Column<Guid>(type: "uuid", nullable: false),
                serial = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                interval_minutes = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_meters", x => x.id);
                table.CheckConstraint("ck_meters_kind", "kind IN ('main', 'hvac', 'lighting', 'plug', 'process')");
                table.CheckConstraint("ck_meters_interval_minutes", "interval_minutes IN (5, 10, 15, 30, 60)");
                table.CheckConstraint("ck_meters_serial", "serial ~ '^[A-Z0-9][A-Z0-9-]{2,39}$'");
                table.ForeignKey(
                    name: "fk_meters_buildings_organization_id_building_id",
                    columns: x => new { x.organization_id, x.building_id },
                    principalTable: "buildings",
                    principalColumns: ["organization_id", "id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ux_buildings_organization_name",
            table: "buildings",
            columns: ["organization_id", "name"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_meters_organization_serial",
            table: "meters",
            columns: ["organization_id", "serial"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_meters_organization_building",
            table: "meters",
            columns: ["organization_id", "building_id"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "meters");
        migrationBuilder.DropTable(name: "buildings");
    }
}
