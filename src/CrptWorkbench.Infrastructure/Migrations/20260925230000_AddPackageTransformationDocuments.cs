using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20260925230000_AddPackageTransformationDocuments")]
public sealed class AddPackageTransformationDocuments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "PackageTransformationDocuments", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
            Mode = table.Column<int>(type: "INTEGER", nullable: false),
            ProductGroup = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
            Action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
            PackageCode = table.Column<string>(type: "TEXT", maxLength: 74, nullable: false),
            ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
            DocumentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
            State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
            DocumentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
            RemoteStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
            Error = table.Column<string>(type: "TEXT", nullable: true),
            CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
            SubmittedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
            LastCheckedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
            NextCheckAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
            TrackingUntilUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
            LeaseUntilUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
            PollCount = table.Column<int>(type: "INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_PackageTransformationDocuments", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_PackageTransformationDocuments_ProfileId", table: "PackageTransformationDocuments", column: "ProfileId");
        migrationBuilder.CreateIndex(name: "IX_PackageTransformationDocuments_NextCheckAtUnixMs", table: "PackageTransformationDocuments", column: "NextCheckAtUnixMs");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("PackageTransformationDocuments");
}
