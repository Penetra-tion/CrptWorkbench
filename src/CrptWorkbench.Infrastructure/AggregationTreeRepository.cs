using System.Text;
using CrptWorkbench.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

// The persistent frontier and graph are queried in bounded pages; they are never EF-tracked collections.
public sealed class AggregationTreeRepository(
    IDbContextFactory<WorkbenchDbContext> factory, IAggregationTreeSourceStore files) : IAggregationTreeRepository
{
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = new SqliteConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct);
        await Execute(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000; PRAGMA temp_store=FILE; PRAGMA cache_size=-8192;", ct);
        return connection;
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, params object?[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue($"@p{i}", args[i] is Guid id ? id.ToString("D") : args[i] ?? DBNull.Value);
        return command;
    }
    private static async Task<int> Execute(SqliteConnection db, string sql, CancellationToken ct, params object?[] args)
    {
        await using var command = Command(db, sql, args);
        return await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<long> Scalar(SqliteConnection db, string sql, CancellationToken ct, params object?[] args)
    {
        await using var command = Command(db, sql, args);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
    private const string JobColumns = "Id,ProfileId,Mode,Name,State,IncludeSiblings,SourceBytes,SourceOffset,LinesRead,RejectedRows,NodesTotal,NodesLoaded,NodeErrors,Revision,CreatedAtUnixMs,Error";
    private static AggregationTreeJob ReadJob(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)),
        (CrptIntegrationMode)r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetInt64(6), r.GetInt64(7),
        r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(14)), r.IsDBNull(15) ? null : r.GetString(15));

    public async Task CreateAsync(Guid id, Guid profileId, CrptIntegrationMode mode, string name, long bytes, CancellationToken ct)
    {
        await using var db = await Open(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // A unique millisecond cursor also gives append paging a stable boundary when jobs are created together.
        await Execute(db, """
            INSERT INTO AggregationTreeJobs(Id,ProfileId,Mode,Name,SourceBytes,CreatedAtUnixMs,UpdatedAtUnixMs)
            VALUES(@p0,@p1,@p2,@p3,@p4,MAX(@p5,COALESCE((SELECT MAX(CreatedAtUnixMs)+1 FROM AggregationTreeJobs),@p5)),@p5)
            """, ct, id, profileId, (int)mode, name, bytes, now);
    }
    public async Task<AggregationTreeJob?> GetAsync(Guid profileId, Guid id, CrptIntegrationMode mode, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await using var command = Command(db, $"SELECT {JobColumns} FROM AggregationTreeJobs WHERE Id=@p0 AND ProfileId=@p1 AND Mode=@p2", id, profileId, (int)mode);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }
    public async Task<IReadOnlyList<AggregationTreeJob>> GetManyAsync(Guid profileId, IReadOnlyList<Guid> ids, CrptIntegrationMode mode, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await Open(ct);
        var result = new List<AggregationTreeJob>();
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            var args = new List<object?> { profileId, (int)mode };
            args.AddRange(chunk.Select(id => (object?)id));
            var names = string.Join(',', Enumerable.Range(2, chunk.Length).Select(i => $"@p{i}"));
            await using var command = Command(db, $"SELECT {JobColumns} FROM AggregationTreeJobs WHERE ProfileId=@p0 AND Mode=@p1 AND Id IN ({names})", args.ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(ReadJob(reader));
        }
        return result;
    }
    public async Task<IReadOnlyList<AggregationTreeJob>> ListAsync(Guid profileId, CrptIntegrationMode mode, long? before, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await using var command = Command(db, $"SELECT {JobColumns} FROM AggregationTreeJobs WHERE ProfileId=@p0 AND Mode=@p1 AND (@p2 IS NULL OR CreatedAtUnixMs<@p2) ORDER BY CreatedAtUnixMs DESC,Id DESC LIMIT 100", profileId, (int)mode, before);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AggregationTreeJob>();
        while (await reader.ReadAsync(ct)) result.Add(ReadJob(reader));
        return result;
    }
    public async Task<AggregationTreeJob?> NextAsync(CrptIntegrationMode mode, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await using var command = Command(db, $"SELECT {JobColumns} FROM AggregationTreeJobs WHERE Mode=@p0 AND State IN ('Queued','Running','PauseRequested','CancelRequested') ORDER BY CreatedAtUnixMs LIMIT 1", (int)mode);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }
    public async Task<bool> TransitionAsync(Guid profileId, Guid id, CrptIntegrationMode mode, IReadOnlyList<string> from, string to, string? error, CancellationToken ct)
    {
        if (from.Count == 0) return false;
        await using var db = await Open(ct);
        var args = new List<object?> { id, profileId, (int)mode, to, error, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        args.AddRange(from);
        var names = string.Join(',', Enumerable.Range(6, from.Count).Select(x => $"@p{x}"));
        if (to == "Queued") await Execute(db, "BEGIN IMMEDIATE", ct);
        try
        {
            var changed = await Execute(db, $"UPDATE AggregationTreeJobs SET State=@p3,Error=@p4,UpdatedAtUnixMs=@p5 WHERE Id=@p0 AND ProfileId=@p1 AND Mode=@p2 AND State IN ({names})", ct, args.ToArray()) > 0;
            if (to == "Queued")
            {
                if (changed) await Execute(db, """
                    UPDATE AggregationTreeNodes SET State='Pending',BatchId=NULL WHERE JobId=@p0 AND State='Fetching';
                    DELETE FROM AggregationTreeStageNodes WHERE JobId=@p0;
                    DELETE FROM AggregationTreeStageEdges WHERE JobId=@p0;
                    """, ct, id);
                await Execute(db, "COMMIT", ct);
            }
            return changed;
        }
        catch { if (to == "Queued") await Execute(db, "ROLLBACK", CancellationToken.None); throw; }
    }
    public async Task<bool> StartAsync(Guid profileId, Guid id, CrptIntegrationMode mode, bool includeSiblings, CancellationToken ct)
    {
        await using var db = await Open(ct);
        return await Execute(db, "UPDATE AggregationTreeJobs SET State='Queued',IncludeSiblings=@p3 WHERE Id=@p0 AND ProfileId=@p1 AND Mode=@p2 AND State='Ready'", ct, id, profileId, (int)mode, includeSiblings) > 0;
    }
    public async Task RecoverAsync(CrptIntegrationMode mode, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await Execute(db, """
            BEGIN IMMEDIATE;
            DROP TABLE IF EXISTS temp.RecoveryJobs;
            CREATE TEMP TABLE RecoveryJobs AS SELECT Id FROM AggregationTreeJobs WHERE Mode=@p0
              AND State IN ('Running','Queued','PauseRequested','CancelRequested');
            UPDATE AggregationTreeJobs SET State=CASE WHEN State='CancelRequested' THEN 'Canceled' ELSE 'Paused' END
              WHERE Mode=@p0 AND State IN ('Running','Queued','PauseRequested','CancelRequested');
            UPDATE AggregationTreeNodes SET State='Pending',BatchId=NULL WHERE State='Fetching'
              AND JobId IN (SELECT Id FROM AggregationTreeJobs WHERE Mode=@p0);
            DELETE FROM AggregationTreeStageNodes WHERE JobId IN (SELECT Id FROM AggregationTreeJobs WHERE Mode=@p0);
            DELETE FROM AggregationTreeStageEdges WHERE JobId IN (SELECT Id FROM AggregationTreeJobs WHERE Mode=@p0);
            COMMIT;
            """, ct, (int)mode);
        string? after = null;
        while (true)
        {
            var ids = new List<string>(100);
            await using (var command = Command(db, "SELECT Id FROM RecoveryJobs WHERE @p0 IS NULL OR Id>@p0 ORDER BY Id LIMIT 100", after))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
            if (ids.Count == 0) break;
            foreach (var id in ids) await ValidateAsync(Guid.Parse(id), ct);
            after = ids[^1];
        }
        await Execute(db, "DROP TABLE RecoveryJobs", ct);
    }
    public async Task ImportAsync(Guid id, long offset, IReadOnlyList<AggregationSeed> rows, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await Execute(db, "BEGIN IMMEDIATE", ct);
        try
        {
            await using var input = Command(db, "INSERT OR IGNORE INTO AggregationTreeInputs(JobId,Line,Code,Value,Error) VALUES(@p0,@p1,@p2,@p3,@p4)", id, 0L, null, null, null);
            await using var node = Command(db, """
                INSERT INTO AggregationTreeNodes(JobId,Code,Requested,Descend) VALUES(@p0,@p1,1,1)
                ON CONFLICT(JobId,Code) DO UPDATE SET Requested=1,Descend=1
                """, id, "");
            input.Prepare();
            node.Prepare();
            long rejected = 0;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                input.Parameters[1].Value = row.Line;
                input.Parameters[2].Value = (object?)row.Code ?? DBNull.Value;
                input.Parameters[3].Value = (object?)row.Value ?? DBNull.Value;
                input.Parameters[4].Value = (object?)row.Error ?? DBNull.Value;
                var added = await input.ExecuteNonQueryAsync(ct);
                if (added == 0) continue;
                if (row.Code is not null)
                {
                    node.Parameters[1].Value = row.Code;
                    await node.ExecuteNonQueryAsync(ct);
                }
                else if (row.Error is not null) rejected++;
            }
            await Execute(db, "UPDATE AggregationTreeJobs SET SourceOffset=@p1,LinesRead=MAX(LinesRead,@p2),RejectedRows=RejectedRows+@p3,Revision=Revision+1 WHERE Id=@p0", ct, id, offset, rows.Count == 0 ? 0 : rows[^1].Line, rejected);
            await Execute(db, "COMMIT", ct);
        }
        catch { await Execute(db, "ROLLBACK", CancellationToken.None); throw; }
    }

    private static async Task Expand(SqliteConnection db, Guid id, CancellationToken ct)
    {
        // Expanding cached nodes separately is essential when an ancestor later appears as another seed.
        while (true)
        {
            var work = await Scalar(db, """
                SELECT COUNT(*) FROM (
                  SELECT Code FROM AggregationTreeNodes WHERE JobId=@p0 AND State='Loaded' AND ExpandedParent=0
                  UNION ALL
                  SELECT Code FROM AggregationTreeNodes WHERE JobId=@p0 AND State='Loaded' AND Descend=1 AND ExpandedChildren=0 LIMIT 1)
                """, ct, id);
            if (work == 0) break;
            // Keep the bounded expansion outside the edge lookup. SQLite may otherwise
            // scan every job edge for each expansion row (quadratic on wide trees).
            await Execute(db, """
                DROP TABLE IF EXISTS temp.Expansion;
                CREATE TEMP TABLE Expansion AS SELECT Code,Parent,Descend FROM AggregationTreeNodes
                  WHERE JobId=@p0 AND State='Loaded' AND ExpandedParent=0
                  UNION SELECT Code,Parent,Descend FROM AggregationTreeNodes
                  WHERE JobId=@p0 AND State='Loaded' AND Descend=1 AND ExpandedChildren=0 LIMIT 1000;
                INSERT INTO AggregationTreeNodes(JobId,Code,Descend)
                  SELECT @p0,Parent,(SELECT IncludeSiblings FROM AggregationTreeJobs WHERE Id=@p0) FROM Expansion WHERE Parent IS NOT NULL
                  ON CONFLICT(JobId,Code) DO UPDATE SET Descend=MAX(Descend,excluded.Descend);
                INSERT INTO AggregationTreeNodes(JobId,Code,Descend)
                  SELECT @p0,e.Child,1 FROM Expansion x CROSS JOIN AggregationTreeEdges e
                  WHERE x.Descend=1 AND e.JobId=@p0 AND e.Parent=x.Code AND e.FromParent=1
                  ON CONFLICT(JobId,Code) DO UPDATE SET Descend=1;
                UPDATE AggregationTreeNodes SET ExpandedParent=1,
                  ExpandedChildren=CASE WHEN Code IN (SELECT Code FROM Expansion WHERE Descend=1) THEN 1 ELSE ExpandedChildren END
                  WHERE JobId=@p0 AND Code IN (SELECT Code FROM Expansion);
                DROP TABLE Expansion;
                """, ct, id);
        }
    }
    public async Task<AggregationFetchBatch?> TakeBatchAsync(Guid id, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await Execute(db, "BEGIN IMMEDIATE", ct);
        try
        {
            if (await Scalar(db, "SELECT COUNT(*) FROM AggregationTreeJobs WHERE Id=@p0 AND State IN ('Queued','Running')", ct, id) == 0)
            { await Execute(db, "COMMIT", ct); return null; }
            await Expand(db, id, ct);
            var codes = new List<string>(1000);
            await using (var command = Command(db, "SELECT Code FROM AggregationTreeNodes WHERE JobId=@p0 AND State='Pending' ORDER BY Code COLLATE BINARY LIMIT 1000", id))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) codes.Add(reader.GetString(0));
            if (codes.Count == 0) { await Execute(db, "COMMIT", ct); return null; }
            var batchId = Guid.NewGuid();
            // The immediate transaction keeps the same indexed first 1000 rows claimed as a set.
            await Execute(db, """
                UPDATE AggregationTreeNodes SET State='Fetching',BatchId=@p1 WHERE JobId=@p0 AND Code IN (
                  SELECT Code FROM AggregationTreeNodes WHERE JobId=@p0 AND State='Pending' ORDER BY Code COLLATE BINARY LIMIT 1000)
                """, ct, id, batchId);
            await Execute(db, "COMMIT", ct);
            return new(batchId, codes);
        }
        catch { await Execute(db, "ROLLBACK", CancellationToken.None); throw; }
    }
    public async Task StageChildrenAsync(Guid jobId, Guid batchId, string code, IReadOnlyList<string> children, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await Execute(db, "BEGIN IMMEDIATE", ct);
        try
        {
            await using var insert = Command(db, "INSERT OR IGNORE INTO AggregationTreeStageEdges(JobId,BatchId,Parent,Child) VALUES(@p0,@p1,@p2,@p3)", jobId, batchId, code, "");
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                insert.Parameters[3].Value = child;
                await insert.ExecuteNonQueryAsync(ct);
            }
            await Execute(db, "COMMIT", ct);
        }
        catch { await Execute(db, "ROLLBACK", CancellationToken.None); throw; }
    }
    public async Task StageNodeAsync(Guid jobId, Guid batchId, AggregationResponseNode node, CancellationToken ct)
        => await StageNodesAsync(jobId, batchId, [node], ct);

    public async Task StageNodesAsync(Guid jobId, Guid batchId, IReadOnlyList<AggregationResponseNode> nodes, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await Execute(db, "BEGIN IMMEDIATE", ct);
        try
        {
            await using var insert = Command(db, """
                INSERT INTO AggregationTreeStageNodes(JobId,BatchId,Code,Cis,Parent,PackageType,Status,ChildrenKnown,ChildCount,Error)
                  VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9)
                """, jobId, batchId, "", null, null, null, null, false, 0L, null);
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();
                object?[] values = [node.Code, node.Cis, node.Parent, node.PackageType, node.Status, node.ChildrenKnown, node.ChildCount, node.Error];
                for (var i = 0; i < values.Length; i++) insert.Parameters[i + 2].Value = values[i] ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(ct);
            }
            await Execute(db, "COMMIT", ct);
        }
        catch { await Execute(db, "ROLLBACK", CancellationToken.None); throw; }
    }
    public async Task PublishAsync(Guid jobId, AggregationFetchBatch batch, string? error, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await Execute(db, "BEGIN IMMEDIATE", ct);
        try
        {
            if (error is null)
            {
                await Execute(db, """
                    INSERT INTO AggregationTreeEdges(JobId,Parent,Child,FromParent)
                      SELECT JobId,Parent,Child,1 FROM AggregationTreeStageEdges WHERE JobId=@p0 AND BatchId=@p1
                      ON CONFLICT(JobId,Parent,Child) DO UPDATE SET FromParent=1;
                    INSERT INTO AggregationTreeEdges(JobId,Parent,Child,FromChild)
                      SELECT JobId,Parent,Code,1 FROM AggregationTreeStageNodes WHERE JobId=@p0 AND BatchId=@p1 AND Parent IS NOT NULL AND Error IS NULL
                      ON CONFLICT(JobId,Parent,Child) DO UPDATE SET FromChild=1;
                    UPDATE AggregationTreeNodes AS n SET
                      Parent=s.Parent,PackageType=s.PackageType,Status=s.Status,ChildrenKnown=s.ChildrenKnown,
                      ChildCount=(SELECT COUNT(*) FROM AggregationTreeStageEdges e WHERE e.JobId=@p0 AND e.BatchId=@p1 AND e.Parent=n.Code),
                      Error=s.Error,State=CASE WHEN s.Error IS NULL THEN 'Loaded' ELSE 'Error' END,BatchId=NULL
                      FROM AggregationTreeStageNodes s WHERE n.JobId=@p0 AND n.BatchId=@p1 AND s.JobId=@p0 AND s.BatchId=@p1 AND n.Code=s.Code;
                    """, ct, jobId, batch.Id);
            }
            await Execute(db, "UPDATE AggregationTreeNodes SET State='Error',Error=@p2,BatchId=NULL WHERE JobId=@p0 AND BatchId=@p1", ct, jobId, batch.Id, error ?? "Ответ по коду отсутствует.");
            await ClearStage(db, jobId, batch.Id, ct);
            await Execute(db, "UPDATE AggregationTreeJobs SET Revision=Revision+1 WHERE Id=@p0", ct, jobId);
            await Execute(db, "COMMIT", ct);
        }
        catch { await Execute(db, "ROLLBACK", CancellationToken.None); throw; }
    }
    private static Task<int> ClearStage(SqliteConnection db, Guid jobId, Guid batchId, CancellationToken ct) => Execute(db,
        "DELETE FROM AggregationTreeStageNodes WHERE JobId=@p0 AND BatchId=@p1; DELETE FROM AggregationTreeStageEdges WHERE JobId=@p0 AND BatchId=@p1", ct, jobId, batchId);
    public async Task ResetBatchAsync(Guid jobId, AggregationFetchBatch batch, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await Execute(db, "BEGIN IMMEDIATE", ct);
        try
        {
            await Execute(db, "UPDATE AggregationTreeNodes SET State='Pending',BatchId=NULL WHERE JobId=@p0 AND BatchId=@p1", ct, jobId, batch.Id);
            await ClearStage(db, jobId, batch.Id, ct);
            await Execute(db, "COMMIT", ct);
        }
        catch { await Execute(db, "ROLLBACK", CancellationToken.None); throw; }
    }

    public async Task ValidateAsync(Guid jobId, CancellationToken ct)
    {
        // CROSS JOIN fixes the loop order: use a code's indexed edges before looking up its parent.
        // Otherwise SQLite can choose a full scan of all loaded parents for every node.
        await using var db = await Open(ct);
        await Execute(db, "BEGIN IMMEDIATE", ct);
        try
        {
            await Execute(db, """
                UPDATE AggregationTreeEdges SET Confirmed=0 WHERE JobId=@p0;
                UPDATE AggregationTreeNodes AS n SET Error='Код указан у нескольких родителей.',State='Error'
                  WHERE n.JobId=@p0 AND n.State='Loaded' AND
                  (SELECT COUNT(*) FROM AggregationTreeEdges e JOIN AggregationTreeNodes p ON p.JobId=e.JobId AND p.Code=e.Parent
                    WHERE e.JobId=@p0 AND e.Child=n.Code)>1;
                UPDATE AggregationTreeNodes AS n SET Error='Противоречие между родителем и составом упаковки.',State='Error'
                  WHERE n.JobId=@p0 AND n.State='Loaded' AND EXISTS (
                    SELECT 1 FROM AggregationTreeEdges e CROSS JOIN AggregationTreeNodes p
                    WHERE e.JobId=@p0 AND e.Child=n.Code AND p.JobId=e.JobId AND p.Code=e.Parent AND p.State='Loaded' AND
                    ((n.Parent IS NOT NULL AND n.Parent<>e.Parent) OR (p.ChildrenKnown=1 AND e.FromParent=0)));
                UPDATE AggregationTreeEdges AS e SET Confirmed=1
                  WHERE e.JobId=@p0 AND EXISTS(SELECT 1 FROM AggregationTreeNodes p,AggregationTreeNodes c
                    WHERE p.JobId=e.JobId AND p.Code=e.Parent AND c.JobId=e.JobId AND c.Code=e.Child
                    AND p.State='Loaded' AND c.State='Loaded' AND (c.Parent IS NULL OR c.Parent=e.Parent)
                    AND (p.ChildrenKnown=0 OR e.FromParent=1));
                UPDATE AggregationTreeNodes SET Valid=0 WHERE JobId=@p0;
                WITH RECURSIVE Reach(Code) AS (
                  SELECT n.Code FROM AggregationTreeNodes n WHERE n.JobId=@p0 AND n.State='Loaded' AND n.Parent IS NULL
                    AND NOT EXISTS(SELECT 1 FROM AggregationTreeEdges e WHERE e.JobId=@p0 AND e.Child=n.Code)
                  UNION
                  SELECT e.Child FROM Reach r CROSS JOIN AggregationTreeEdges e WHERE e.Parent=r.Code AND e.JobId=@p0 AND e.Confirmed=1
                ) UPDATE AggregationTreeNodes SET Valid=1 WHERE JobId=@p0 AND Code IN (SELECT Code FROM Reach);
                UPDATE AggregationTreeJobs SET NodeErrors=(SELECT COUNT(*) FROM AggregationTreeNodes
                  WHERE JobId=@p0 AND (State='Error' OR (State='Loaded' AND Valid=0))) WHERE Id=@p0;
                """, ct, jobId);
            await Execute(db, "COMMIT", ct);
        }
        catch { await Execute(db, "ROLLBACK", CancellationToken.None); throw; }
    }
    public async Task<AggregationNodePage> NodesAsync(Guid jobId, string? parent, string? cursor, CancellationToken ct)
    {
        await using var db = await Open(ct);
        var from = parent is null ? "AggregationTreeNodes n"
            : "AggregationTreeEdges branch CROSS JOIN AggregationTreeNodes n ON n.JobId=branch.JobId AND n.Code=branch.Child";
        var filter = parent is null
            ? "n.JobId=@p0 AND (NOT EXISTS(SELECT 1 FROM AggregationTreeEdges e JOIN AggregationTreeNodes p ON p.JobId=e.JobId AND p.Code=e.Parent WHERE e.JobId=n.JobId AND e.Child=n.Code) OR (n.Valid=0 AND EXISTS(SELECT 1 FROM AggregationTreeJobs j WHERE j.Id=n.JobId AND j.State IN ('Paused','Completed','Partial','Canceled'))))"
            : "branch.JobId=@p0 AND branch.Parent=@p2";
        var order = parent is null ? "n.Code" : "branch.Child";
        var after = cursor is null ? "" : $" AND {order}>@p1 COLLATE BINARY";
        await using var command = Command(db, $"""
            SELECT n.Code,n.PackageType,n.Status,n.State,n.Error,n.ChildCount,n.Valid,n.Requested,
              EXISTS(SELECT 1 FROM AggregationTreeEdges e JOIN AggregationTreeNodes c ON c.JobId=e.JobId AND c.Code=e.Child WHERE e.JobId=n.JobId AND e.Parent=n.Code)
            FROM {from} WHERE {filter}{after}
            ORDER BY {order} COLLATE BINARY LIMIT 101
            """, jobId, cursor, parent);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AggregationJobNode>(101);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? (reader.GetString(3) == "Loaded" && !reader.GetBoolean(6) ? "Путь ещё не подтверждён: цикл или недоступен родитель." : null) : reader.GetString(4),
            reader.GetInt64(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8)));
        var next = result.Count > 100 ? result[99].Code : null;
        if (result.Count > 100) result.RemoveAt(100);
        return new(result, next);
    }
    public async Task<IReadOnlyList<string>> TypesAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await using var command = Command(db, "SELECT DISTINCT COALESCE(PackageType,'') FROM AggregationTreeNodes WHERE JobId=@p0 AND State='Loaded' ORDER BY 1 COLLATE BINARY", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var types = new List<string>();
        while (await reader.ReadAsync(ct)) types.Add(reader.GetString(0));
        return types;
    }
    public async Task<AggregationRejectedPage> RejectedAsync(Guid jobId, long? after, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await using var command = Command(db, "SELECT Line,Value,Error FROM AggregationTreeInputs WHERE JobId=@p0 AND Error IS NOT NULL AND Line>@p1 ORDER BY Line LIMIT 101", jobId, after ?? 0);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AggregationRejectedRow>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetString(2)));
        var next = result.Count > 100 ? result[99].Line : (long?)null;
        if (result.Count > 100) result.RemoveAt(100);
        return new(result, next);
    }

    public async Task<AggregationExportFile> ExportAsync(Guid profileId, Guid jobId, CrptIntegrationMode mode, AggregationExportOptions options, CancellationToken ct)
    {
        var job = await GetAsync(profileId, jobId, mode, ct) ?? throw new InvalidOperationException("Задание не найдено.");
        if (job.State is not ("Paused" or "Completed" or "Partial" or "Canceled")) throw new InvalidOperationException("Сначала остановите задание.");
        if (!options.FullTree && options.PackageTypes is not { Count: > 0 }) throw new ArgumentException("Выберите хотя бы один тип упаковки.");
        await ValidateAsync(jobId, ct);
        var selected = (options.PackageTypes ?? []).ToHashSet(StringComparer.Ordinal);
        var exportId = Guid.NewGuid();
        var path = files.ExportPath(jobId, exportId);
        var tempDb = path + ".sqlite";
        long lines = 0;
        try
        {
            await using var snapshot = await Open(ct);
            // The read transaction pins the validated graph even if the job is resumed during export.
            await using var transaction = snapshot.BeginTransaction(deferred: true);
            await using var unique = new SqliteConnection($"Data Source={tempDb};Pooling=False");
            await unique.OpenAsync(ct);
            await Execute(unique, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-4096; CREATE TABLE Rows(Line TEXT COLLATE BINARY PRIMARY KEY,Position INTEGER NOT NULL); BEGIN", ct);
            await using var lookup = Command(snapshot, """
                SELECT n.PackageType,n.Valid,(SELECT e.Parent FROM AggregationTreeEdges e WHERE e.JobId=@p0 AND e.Child=n.Code AND e.Confirmed=1 LIMIT 1),n.ChildCount
                FROM AggregationTreeNodes n WHERE n.JobId=@p0 AND n.Code=@p1
                """, jobId, "");
            lookup.Transaction = transaction;
            lookup.Prepare();
            await using var insertLine = Command(unique, "INSERT OR IGNORE INTO Rows(Line,Position) VALUES(@p0,@p1)", "", 0L);
            insertLine.Prepare();
            // Cache only ancestors. Leaf rows are consumed once and must not evict shared parents.
            const int ancestorCapacity = 4096;
            var ancestors = new Dictionary<string, (string Type, bool Valid, string? Parent)>(StringComparer.Ordinal);
            var cacheOrder = new Queue<string>();
            string? last = null;
            while (true)
            {
                var leaves = new List<string>(1000);
                var after = last is null ? "" : " AND Code>@p1 COLLATE BINARY";
                await using (var command = Command(snapshot, $"SELECT Code FROM AggregationTreeNodes WHERE JobId=@p0 AND Valid=1 AND ChildCount=0{after} ORDER BY Code COLLATE BINARY LIMIT 1000", jobId, last))
                {
                    command.Transaction = transaction;
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) leaves.Add(reader.GetString(0));
                }
                if (leaves.Count == 0) break;
                foreach (var leaf in leaves)
                {
                    ct.ThrowIfCancellationRequested();
                    var values = new List<string>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    string? code = leaf;
                    var valid = true;
                    while (code is not null)
                    {
                        if (!seen.Add(code)) { valid = false; break; }
                        if (!ancestors.TryGetValue(code, out var node))
                        {
                            lookup.Parameters[1].Value = code;
                            await using var reader = await lookup.ExecuteReaderAsync(ct);
                            if (!await reader.ReadAsync(ct)) { valid = false; break; }
                            node = (reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2));
                            if (reader.GetInt64(3) > 0)
                            {
                                if (ancestors.Count == ancestorCapacity) ancestors.Remove(cacheOrder.Dequeue());
                                ancestors.Add(code, node);
                                cacheOrder.Enqueue(code);
                            }
                        }
                        if (!node.Valid) { valid = false; break; }
                        if (options.FullTree || selected.Contains(node.Type)) values.Add(code);
                        code = node.Parent;
                    }
                    if (valid && values.Count > 0)
                    {
                        insertLine.Parameters[0].Value = string.Join('\t', values);
                        insertLine.Parameters[1].Value = lines;
                        var added = await insertLine.ExecuteNonQueryAsync(ct);
                        if (added > 0) lines++;
                    }
                }
                last = leaves[^1];
            }
            await Execute(unique, "COMMIT", ct);
            await using (var output = new StreamWriter(path + ".tmp", false, new UTF8Encoding(false), 65536))
            await using (var command = Command(unique, "SELECT Line FROM Rows ORDER BY Position"))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) await output.WriteAsync((reader.GetString(0) + "\r\n").AsMemory(), ct);
                await output.FlushAsync(ct);
            }
            File.Move(path + ".tmp", path);
            return new(exportId, jobId, profileId, mode, path, lines);
        }
        catch { File.Delete(path + ".tmp"); File.Delete(path); throw; }
        finally { File.Delete(tempDb); }
    }
    public async Task DeleteAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await Execute(db, "DELETE FROM AggregationTreeStageNodes WHERE JobId=@p0; DELETE FROM AggregationTreeStageEdges WHERE JobId=@p0; DELETE FROM AggregationTreeJobs WHERE Id=@p0 AND State='Deleting'", ct, jobId);
    }
}
