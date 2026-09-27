using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20260915144500_AddTraceXAndFavorites")]
public partial class AddTraceXAndFavorites : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TraceXDatabaseProvider",
            table: "Profiles",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "SqlServer");

        migrationBuilder.CreateTable(
            name: "FavoriteApiOperations",
            columns: table => new
            {
                ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                OperationId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FavoriteApiOperations", x => new { x.ProfileId, x.OperationId });
                table.ForeignKey(
                    name: "FK_FavoriteApiOperations_Profiles_ProfileId",
                    column: x => x.ProfileId,
                    principalTable: "Profiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "FavoriteApiOperations");
        migrationBuilder.DropColumn(name: "TraceXDatabaseProvider", table: "Profiles");
    }
}
