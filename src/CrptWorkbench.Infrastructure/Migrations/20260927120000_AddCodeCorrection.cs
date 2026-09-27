using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20260927120000_AddCodeCorrection")]
public sealed class AddCodeCorrection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite stores Action as unrestricted TEXT already; this metadata-only
        // widening to 32 needs no table rebuild and preserves all tracked GUIDs.
        // CodeCorrection appends enum value 2 without changing existing values.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The SQLite physical schema is identical in both model versions.
    }
}
