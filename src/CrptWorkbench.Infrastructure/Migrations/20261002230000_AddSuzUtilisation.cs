using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20261002230000_AddSuzUtilisation")]
public sealed class AddSuzUtilisation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("SuzUtilisationJobs", table => new
        {
            Id = table.Column<Guid>("TEXT", nullable: false), ProfileId = table.Column<Guid>("TEXT", nullable: false), Mode = table.Column<int>("INTEGER", nullable: false),
            ProductGroup = table.Column<string>("TEXT", nullable: false), PackageType = table.Column<string>("TEXT", nullable: false), Scenario = table.Column<string>("TEXT", nullable: false),
            OmsId = table.Column<string>("TEXT", nullable: false), ConnectionId = table.Column<string>("TEXT", nullable: false), BaseUrl = table.Column<string>("TEXT", nullable: false),
            CertificateThumbprint = table.Column<string>("TEXT", nullable: false), CorrelationId = table.Column<string>("TEXT", nullable: false),
            CodeCount = table.Column<int>("INTEGER", nullable: false), ReportCount = table.Column<int>("INTEGER", nullable: false), CreatedAtUnixMs = table.Column<long>("INTEGER", nullable: false),
            SubmissionOwner = table.Column<Guid>("TEXT", nullable: true)
        }, constraints: table => { table.PrimaryKey("PK_SuzUtilisationJobs", x => x.Id); table.ForeignKey("FK_SuzUtilisationJobs_Profiles_ProfileId", x => x.ProfileId, "Profiles", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable("SuzUtilisationReports", table => new
        {
            Id = table.Column<Guid>("TEXT", nullable: false), JobId = table.Column<Guid>("TEXT", nullable: false), Number = table.Column<int>("INTEGER", nullable: false), CodeCount = table.Column<int>("INTEGER", nullable: false),
            ProtectedPayload = table.Column<string>("TEXT", nullable: false), State = table.Column<string>("TEXT", nullable: false), ReportId = table.Column<string>("TEXT", nullable: true),
            RemoteStatus = table.Column<string>("TEXT", nullable: true), ReceiptState = table.Column<string>("TEXT", nullable: true), Error = table.Column<string>("TEXT", nullable: true),
            SubmittedAtUnixMs = table.Column<long>("INTEGER", nullable: true), LastCheckedAtUnixMs = table.Column<long>("INTEGER", nullable: true), NextCheckAtUnixMs = table.Column<long>("INTEGER", nullable: true), TrackingUntilUnixMs = table.Column<long>("INTEGER", nullable: true),
            TrackingOwner = table.Column<Guid>("TEXT", nullable: true), LeaseUntilUnixMs = table.Column<long>("INTEGER", nullable: true), PollCount = table.Column<int>("INTEGER", nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_SuzUtilisationReports", x => x.Id); table.ForeignKey("FK_SuzUtilisationReports_SuzUtilisationJobs_JobId", x => x.JobId, "SuzUtilisationJobs", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateIndex("IX_SuzUtilisationJobs_ProfileId_Mode_CreatedAtUnixMs", "SuzUtilisationJobs", new[] { "ProfileId", "Mode", "CreatedAtUnixMs" });
        migrationBuilder.CreateIndex("IX_SuzUtilisationReports_JobId_Number", "SuzUtilisationReports", new[] { "JobId", "Number" }, unique: true);
        migrationBuilder.CreateIndex("IX_SuzUtilisationReports_NextCheckAtUnixMs", "SuzUtilisationReports", "NextCheckAtUnixMs");
    }
    protected override void Down(MigrationBuilder migrationBuilder) { migrationBuilder.DropTable("SuzUtilisationReports"); migrationBuilder.DropTable("SuzUtilisationJobs"); }
}
