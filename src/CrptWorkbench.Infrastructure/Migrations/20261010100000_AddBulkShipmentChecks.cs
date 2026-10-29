using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20261010100000_AddBulkShipmentChecks")]
public sealed class AddBulkShipmentChecks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("BulkShipmentChecks", columns: table => new
        {
            ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
            JobId = table.Column<Guid>(type: "TEXT", nullable: false),
            Mode = table.Column<int>(type: "INTEGER", nullable: false),
            Inn = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
            Code = table.Column<string>(type: "TEXT", maxLength: 74, nullable: false),
            State = table.Column<int>(type: "INTEGER", nullable: false),
            CheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
            Reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_BulkShipmentChecks", x => new { x.ProfileId, x.JobId, x.Mode, x.Inn, x.Code });
            table.ForeignKey("FK_BulkShipmentChecks_BulkCodeJobs_JobId", x => x.JobId, "BulkCodeJobs", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateTable("BulkShipmentCheckProgresses", columns: table => new
        {
            ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
            JobId = table.Column<Guid>(type: "TEXT", nullable: false),
            Mode = table.Column<int>(type: "INTEGER", nullable: false),
            Inn = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
            NextBatchNumber = table.Column<long>(type: "INTEGER", nullable: false),
            ByteOffset = table.Column<long>(type: "INTEGER", nullable: false),
            Completed = table.Column<bool>(type: "INTEGER", nullable: false),
            ReviewedRows = table.Column<long>(type: "INTEGER", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_BulkShipmentCheckProgresses", x => new { x.ProfileId, x.JobId, x.Mode, x.Inn });
            table.ForeignKey("FK_BulkShipmentCheckProgresses_BulkCodeJobs_JobId", x => x.JobId, "BulkCodeJobs", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateTable("BulkShipmentCheckCounters", columns: table => new
        {
            ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
            JobId = table.Column<Guid>(type: "TEXT", nullable: false),
            Mode = table.Column<int>(type: "INTEGER", nullable: false),
            Inn = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
            State = table.Column<int>(type: "INTEGER", nullable: false),
            Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
            Count = table.Column<long>(type: "INTEGER", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_BulkShipmentCheckCounters", x => new { x.ProfileId, x.JobId, x.Mode, x.Inn, x.State, x.Status });
            table.ForeignKey("FK_BulkShipmentCheckCounters_BulkCodeJobs_JobId", x => x.JobId, "BulkCodeJobs", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_BulkShipmentChecks_JobId", "BulkShipmentChecks", "JobId");
        migrationBuilder.CreateIndex("IX_BulkShipmentCheckProgresses_JobId", "BulkShipmentCheckProgresses", "JobId");
        migrationBuilder.CreateIndex("IX_BulkShipmentCheckCounters_JobId", "BulkShipmentCheckCounters", "JobId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("BulkShipmentChecks");
        migrationBuilder.DropTable("BulkShipmentCheckProgresses");
        migrationBuilder.DropTable("BulkShipmentCheckCounters");
    }
}
