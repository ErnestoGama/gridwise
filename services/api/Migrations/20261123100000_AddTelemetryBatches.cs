using GridWise.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridWise.Api.Migrations;

/// <summary>
/// Records the outcome of every accepted telemetry batch (<c>telemetry_batches</c>)
/// and the readings it could not store (<c>telemetry_batch_conflicts</c>). A
/// partial unique index on <c>(organization_id, idempotency_key)</c> makes
/// keyed retries safe. Readings themselves are unchanged. Rollback: <c>Down</c>
/// drops both tables, losing the ingestion history but no readings.
/// </summary>
[DbContext(typeof(GridWiseDbContext))]
[Migration("20261123100000_AddTelemetryBatches")]
public partial class AddTelemetryBatches : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "telemetry_batches",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                payload_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                received_count = table.Column<int>(type: "integer", nullable: false),
                inserted_count = table.Column<int>(type: "integer", nullable: false),
                duplicate_count = table.Column<int>(type: "integer", nullable: false),
                conflict_count = table.Column<int>(type: "integer", nullable: false),
                received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_telemetry_batches", x => x.id);
                table.CheckConstraint("ck_telemetry_batches_counts",
                    "received_count > 0 AND inserted_count >= 0 AND duplicate_count >= 0 AND conflict_count >= 0 "
                    + "AND inserted_count + duplicate_count + conflict_count = received_count");
                table.CheckConstraint("ck_telemetry_batches_idempotency_key",
                    "idempotency_key IS NULL OR idempotency_key ~ '^[A-Za-z0-9._:-]{1,64}$'");
                table.CheckConstraint("ck_telemetry_batches_payload_sha256", "payload_sha256 ~ '^[0-9a-f]{64}$'");
                table.ForeignKey(
                    name: "fk_telemetry_batches_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_telemetry_batches_users_submitted_by_user_id",
                    column: x => x.submitted_by_user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "telemetry_batch_conflicts",
            columns: table => new
            {
                batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                reading_index = table.Column<int>(type: "integer", nullable: false),
                meter_id = table.Column<Guid>(type: "uuid", nullable: false),
                sequence = table.Column<long>(type: "bigint", nullable: false),
                code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_telemetry_batch_conflicts", x => new { x.batch_id, x.reading_index });
                table.CheckConstraint("ck_telemetry_batch_conflicts_code", "code IN ('sequence_reused', 'interval_taken')");
                table.CheckConstraint("ck_telemetry_batch_conflicts_reading_index",
                    "reading_index >= 0 AND reading_index < 1000");
                table.ForeignKey(
                    name: "fk_telemetry_batch_conflicts_telemetry_batches_batch_id",
                    column: x => x.batch_id,
                    principalTable: "telemetry_batches",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ux_telemetry_batches_organization_idempotency_key",
            table: "telemetry_batches",
            columns: ["organization_id", "idempotency_key"],
            unique: true,
            filter: "idempotency_key IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_telemetry_batches_organization_id",
            table: "telemetry_batches",
            columns: ["organization_id", "id"]);

        migrationBuilder.CreateIndex(
            name: "ix_telemetry_batches_submitted_by_user_id",
            table: "telemetry_batches",
            column: "submitted_by_user_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "telemetry_batch_conflicts");
        migrationBuilder.DropTable(name: "telemetry_batches");
    }
}
