using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20260915144000_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ApiRequestLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                Environment = table.Column<int>(type: "INTEGER", nullable: false),
                System = table.Column<int>(type: "INTEGER", nullable: false),
                OperationId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                HttpMethod = table.Column<string>(type: "TEXT", nullable: false),
                Url = table.Column<string>(type: "TEXT", nullable: false),
                StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                RequestHeaders = table.Column<string>(type: "TEXT", nullable: true),
                RequestBody = table.Column<string>(type: "TEXT", nullable: true),
                ResponseHeaders = table.Column<string>(type: "TEXT", nullable: true),
                ResponseBody = table.Column<string>(type: "TEXT", nullable: true),
                Success = table.Column<bool>(type: "INTEGER", nullable: false),
                Error = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ApiRequestLogs", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Profiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Inn = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                CertificateThumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                NationalCatalogPartyId = table.Column<string>(type: "TEXT", nullable: true),
                NationalCatalogProductionBaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                TrueApiProductionBaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                SuzOmsId = table.Column<string>(type: "TEXT", nullable: true),
                SuzConnectionId = table.Column<string>(type: "TEXT", nullable: true),
                SuzProductionBaseUrl = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_Profiles", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ProtectedSecrets",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                ProtectedValue = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ProtectedSecrets", x => x.Key));

        migrationBuilder.CreateIndex(name: "IX_ApiRequestLogs_CorrelationId", table: "ApiRequestLogs", column: "CorrelationId");
        migrationBuilder.CreateIndex(name: "IX_ApiRequestLogs_CreatedAtUnixMs", table: "ApiRequestLogs", column: "CreatedAtUnixMs");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ApiRequestLogs");
        migrationBuilder.DropTable(name: "ProtectedSecrets");
        migrationBuilder.DropTable(name: "Profiles");
    }
}
