using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20261003090000_AddSuzBusinessPlace")]
public sealed class AddSuzBusinessPlace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Kpp", "Profiles", type: "TEXT", maxLength: 9, nullable: true);
        migrationBuilder.AddColumn<string>("FiasId", "Profiles", type: "TEXT", maxLength: 36, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("Kpp", "Profiles");
        migrationBuilder.DropColumn("FiasId", "Profiles");
    }
}
