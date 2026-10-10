using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CleanArchitecture.Blazor.Migrators.MSSQL.Migrations
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
                WITH ranked AS (
                    SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [TenantId], [UserId] ORDER BY [Id]) AS rn
                    FROM [TenantUsers]
                    WHERE [TenantId] IS NOT NULL AND [UserId] IS NOT NULL
                )
                DELETE FROM ranked WHERE rn > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_TenantUsers_TenantId",
                table: "TenantUsers");

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_TenantId_UserId",
                table: "TenantUsers",
                columns: new[] { "TenantId", "UserId" },
                unique: true,
                filter: "[TenantId] IS NOT NULL AND [UserId] IS NOT NULL");
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
