using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20260927090000_AddPackageAggregation")]
public sealed class AddPackageAggregation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "OperationKind", table: "PackageTransformationDocuments",
            type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(name: "PackageType", table: "PackageTransformationDocuments",
            type: "TEXT", maxLength: 16, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PackageType", table: "PackageTransformationDocuments");
        migrationBuilder.DropColumn(name: "OperationKind", table: "PackageTransformationDocuments");
    }
}
