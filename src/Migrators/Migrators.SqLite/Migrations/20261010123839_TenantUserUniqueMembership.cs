using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CleanArchitecture.Blazor.Migrators.SqLite.Migrations
{
    /// <inheritdoc />
    public partial class TenantUserUniqueMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pass 54. An existing database may already hold duplicate memberships - nothing prevented
            // them until now - and the unique index below cannot be created over them. Keep the row
            // with the lowest Id in each (TenantId, UserId) group and delete the rest. Rows with a null
            // in either column are untouched: they can never be equal, so the index does not
            // constrain them.
            migrationBuilder.Sql("""
                DELETE FROM "TenantUsers"
                WHERE EXISTS (
                    SELECT 1 FROM "TenantUsers" AS b
                    WHERE b."TenantId" = "TenantUsers"."TenantId"
                      AND b."UserId" = "TenantUsers"."UserId"
                      AND b."Id" < "TenantUsers"."Id");
                """);

            migrationBuilder.DropIndex(
                name: "IX_TenantUsers_TenantId",
                table: "TenantUsers");

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_TenantId_UserId",
                table: "TenantUsers",
                columns: new[] { "TenantId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantUsers_TenantId_UserId",
                table: "TenantUsers");

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_TenantId",
                table: "TenantUsers",
                column: "TenantId");
        }
    }
}
