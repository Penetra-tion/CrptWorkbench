namespace CrptWorkbench.Application;

public interface ISuzCodeSpreadsheetReader
{
    Task<IReadOnlyList<string>> ReadFirstColumnAsync(Stream stream, CancellationToken cancellationToken = default);
}
