using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CrptWorkbench.Infrastructure.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20261027090000_AddAggregationTreeJobs")]
public sealed class AddAggregationTreeJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE AggregationTreeJobs (
          Id TEXT PRIMARY KEY, ProfileId TEXT NOT NULL, Mode INTEGER NOT NULL, Name TEXT NOT NULL,
          State TEXT NOT NULL DEFAULT 'Ready', IncludeSiblings INTEGER NOT NULL DEFAULT 0,
          SourceBytes INTEGER NOT NULL, SourceOffset INTEGER NOT NULL DEFAULT 0,
          LinesRead INTEGER NOT NULL DEFAULT 0, RejectedRows INTEGER NOT NULL DEFAULT 0,
          NodesTotal INTEGER NOT NULL DEFAULT 0, NodesLoaded INTEGER NOT NULL DEFAULT 0,
          NodeErrors INTEGER NOT NULL DEFAULT 0, Revision INTEGER NOT NULL DEFAULT 0,
          CreatedAtUnixMs INTEGER NOT NULL, UpdatedAtUnixMs INTEGER NOT NULL, Error TEXT NULL);
        CREATE INDEX IX_AggregationTreeJobs_Profile ON AggregationTreeJobs(ProfileId,Mode,CreatedAtUnixMs,Id);
        CREATE INDEX IX_AggregationTreeJobs_State ON AggregationTreeJobs(Mode,State,CreatedAtUnixMs);
        CREATE TABLE AggregationTreeInputs (
          JobId TEXT NOT NULL REFERENCES AggregationTreeJobs(Id) ON DELETE CASCADE,
          Line INTEGER NOT NULL, Code TEXT NULL, Value TEXT NULL, Error TEXT NULL, PRIMARY KEY(JobId,Line));
        CREATE TABLE AggregationTreeNodes (
          JobId TEXT NOT NULL REFERENCES AggregationTreeJobs(Id) ON DELETE CASCADE,
          Code TEXT NOT NULL COLLATE BINARY, State TEXT NOT NULL DEFAULT 'Pending',
          Requested INTEGER NOT NULL DEFAULT 0, Descend INTEGER NOT NULL DEFAULT 0,
          ExpandedParent INTEGER NOT NULL DEFAULT 0, ExpandedChildren INTEGER NOT NULL DEFAULT 0,
          BatchId TEXT NULL, Parent TEXT NULL COLLATE BINARY, PackageType TEXT NULL, Status TEXT NULL,
          ChildrenKnown INTEGER NOT NULL DEFAULT 0, ChildCount INTEGER NOT NULL DEFAULT 0,
          Error TEXT NULL, Valid INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(JobId,Code));
        CREATE INDEX IX_AggregationTreeNodes_Work ON AggregationTreeNodes(JobId,State,Code);
        CREATE INDEX IX_AggregationTreeNodes_Batch ON AggregationTreeNodes(JobId,BatchId);
        CREATE INDEX IX_AggregationTreeNodes_Ascent ON AggregationTreeNodes(JobId,State,ExpandedParent,Code);
        CREATE INDEX IX_AggregationTreeNodes_Descent ON AggregationTreeNodes(JobId,State,Descend,ExpandedChildren,Code);
        CREATE INDEX IX_AggregationTreeNodes_Parent ON AggregationTreeNodes(JobId,Parent,Code);
        CREATE INDEX IX_AggregationTreeNodes_Leaves ON AggregationTreeNodes(JobId,Valid,ChildCount,Code);
        CREATE TABLE AggregationTreeEdges (
          JobId TEXT NOT NULL REFERENCES AggregationTreeJobs(Id) ON DELETE CASCADE,
          Parent TEXT NOT NULL COLLATE BINARY, Child TEXT NOT NULL COLLATE BINARY,
          FromParent INTEGER NOT NULL DEFAULT 0, FromChild INTEGER NOT NULL DEFAULT 0,
          Confirmed INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(JobId,Parent,Child));
        CREATE INDEX IX_AggregationTreeEdges_Child ON AggregationTreeEdges(JobId,Child,Parent);
        CREATE TABLE AggregationTreeStageNodes (
          JobId TEXT NOT NULL, BatchId TEXT NOT NULL, Code TEXT NOT NULL COLLATE BINARY,
          Cis TEXT NULL, Parent TEXT NULL, PackageType TEXT NULL, Status TEXT NULL,
          ChildrenKnown INTEGER NOT NULL, ChildCount INTEGER NOT NULL, Error TEXT NULL,
          PRIMARY KEY(JobId,BatchId,Code));
        CREATE TABLE AggregationTreeStageEdges (
          JobId TEXT NOT NULL, BatchId TEXT NOT NULL, Parent TEXT NOT NULL COLLATE BINARY,
          Child TEXT NOT NULL COLLATE BINARY, PRIMARY KEY(JobId,BatchId,Parent,Child));
        CREATE TRIGGER AggregationTreeNodes_Added AFTER INSERT ON AggregationTreeNodes BEGIN
          UPDATE AggregationTreeJobs SET NodesTotal=NodesTotal+1 WHERE Id=NEW.JobId;
        END;
        CREATE TRIGGER AggregationTreeNodes_State AFTER UPDATE OF State ON AggregationTreeNodes BEGIN
          UPDATE AggregationTreeJobs SET
            NodesLoaded=NodesLoaded+(NEW.State='Loaded')-(OLD.State='Loaded'),
            NodeErrors=NodeErrors+(NEW.State='Error')-(OLD.State='Error') WHERE Id=NEW.JobId;
        END;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE AggregationTreeStageEdges;
        DROP TABLE AggregationTreeStageNodes;
        DROP TABLE AggregationTreeEdges;
        DROP TABLE AggregationTreeNodes;
        DROP TABLE AggregationTreeInputs;
        DROP TABLE AggregationTreeJobs;
        """);
}
