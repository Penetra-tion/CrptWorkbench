using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20260922120000_RemoveNationalCatalogLegacyCredentials")]
public partial class RemoveNationalCatalogLegacyCredentials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE Profiles DROP COLUMN NationalCatalogPartyId");
        migrationBuilder.Sql("DELETE FROM ProtectedSecrets WHERE Key LIKE 'profile:%:Production:national-catalog:api-key'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE Profiles ADD COLUMN NationalCatalogPartyId TEXT NULL");
    }
}
