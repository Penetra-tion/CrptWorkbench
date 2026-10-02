namespace CrptWorkbench.Domain;

public sealed record RecordPageCursor(long CreatedAtUnixMs, IReadOnlyList<Guid> BoundaryIds);
public sealed record RecordPage<T>(IReadOnlyList<T> Items, RecordPageCursor? NextCursor, long? TotalCount)
{
    public const int Size = 100;
}
public sealed record FilePageCursor(long BatchNumber, long ByteOffset);
public sealed record FileRecordPage(IReadOnlyList<string> Items, FilePageCursor? NextCursor);
