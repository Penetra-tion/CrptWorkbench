using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20260928150000_AddBulkCodeJobs")]
public sealed class AddBulkCodeJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("BulkCodeJobs", table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
            Mode = table.Column<int>(type: "INTEGER", nullable: false),
            FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
            State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
            FileBytes = table.Column<long>(type: "INTEGER", nullable: false),
            NextOffset = table.Column<long>(type: "INTEGER", nullable: false),
            NextBatchNumber = table.Column<long>(type: "INTEGER", nullable: false),
            LinesRead = table.Column<long>(type: "INTEGER", nullable: false),
            CodesCompleted = table.Column<long>(type: "INTEGER", nullable: false),
            CodesFound = table.Column<long>(type: "INTEGER", nullable: false),
            CodeErrors = table.Column<long>(type: "INTEGER", nullable: false),
            FailedBatches = table.Column<long>(type: "INTEGER", nullable: false),
            Error = table.Column<string>(type: "TEXT", nullable: true),
            CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_BulkCodeJobs", x => x.Id);
            table.ForeignKey("FK_BulkCodeJobs_Profiles_ProfileId", x => x.ProfileId, "Profiles", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_BulkCodeJobs_ProfileId", "BulkCodeJobs", "ProfileId");
        migrationBuilder.CreateIndex("IX_BulkCodeJobs_State", "BulkCodeJobs", "State");
        migrationBuilder.CreateIndex("IX_BulkCodeJobs_CreatedAtUnixMs", "BulkCodeJobs", "CreatedAtUnixMs");

        migrationBuilder.CreateTable("BulkCodeBatches", table => new
        {
            JobId = table.Column<Guid>(type: "TEXT", nullable: false),
            Number = table.Column<long>(type: "INTEGER", nullable: false),
            StartOffset = table.Column<long>(type: "INTEGER", nullable: false),
            EndOffset = table.Column<long>(type: "INTEGER", nullable: false),
            CodeCount = table.Column<int>(type: "INTEGER", nullable: false),
            State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
            Error = table.Column<string>(type: "TEXT", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_BulkCodeBatches", x => new { x.JobId, x.Number });
            table.ForeignKey("FK_BulkCodeBatches_BulkCodeJobs_JobId", x => x.JobId, "BulkCodeJobs", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_BulkCodeBatches_JobId_State", "BulkCodeBatches", new[] { "JobId", "State" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("BulkCodeBatches");
        migrationBuilder.DropTable("BulkCodeJobs");
    }
}
