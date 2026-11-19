using GridWise.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridWise.Api.Migrations;

/// <summary>
/// Organizations, users and memberships. Rollback: <c>Down</c> drops the three
/// tables; it is only safe before buildings or meters reference organizations.
/// </summary>
[DbContext(typeof(GridWiseDbContext))]
[Migration("20261119090000_AddIdentityAndOrganizations")]
public partial class AddIdentityAndOrganizations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organizations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_organizations", x => x.id);
                table.CheckConstraint("ck_organizations_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                normalized_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                password_hash = table.Column<string>(type: "text", nullable: false),
                failed_login_count = table.Column<int>(type: "integer", nullable: false),
                lockout_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_users", x => x.id);
                table.CheckConstraint("ck_users_failed_login_count", "failed_login_count >= 0");
            });

        migrationBuilder.CreateTable(
            name: "memberships",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_memberships", x => x.id);
                table.CheckConstraint("ck_memberships_role", "role IN ('operator', 'analyst', 'manager', 'admin')");
                table.ForeignKey(
                    name: "fk_memberships_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_memberships_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ux_organizations_slug",
            table: "organizations",
            column: "slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_users_normalized_email",
            table: "users",
            column: "normalized_email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_memberships_organization_user",
            table: "memberships",
            columns: ["organization_id", "user_id"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_memberships_user_id",
            table: "memberships",
            column: "user_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "memberships");
        migrationBuilder.DropTable(name: "users");
        migrationBuilder.DropTable(name: "organizations");
    }
}
