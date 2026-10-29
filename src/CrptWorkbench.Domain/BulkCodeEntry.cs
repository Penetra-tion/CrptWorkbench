namespace CrptWorkbench.Domain;

public sealed record BulkCodeEntry(string Status, string Code, string? OwnerInn = null, bool OwnerKnown = false);
public sealed record BulkCodeEntryPage(IReadOnlyList<BulkCodeEntry> Items, FilePageCursor? NextCursor);
