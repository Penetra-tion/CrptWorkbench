using System.Text.Json;

namespace CrptWorkbench.Application;

public sealed record MercuryVsdInfoResult(string Uid, string? Status, string? IssueDate,
    IReadOnlyList<string> BatchNumbers, string? Gtin, string? ProductName, string? Volume,
    string? Expiry, IReadOnlyList<string> Packages)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static MercuryVsdInfoResult? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<MercuryVsdInfoResult>(json, Options);
            return result is { Uid.Length: 39, BatchNumbers: not null, Packages: not null } ? result : null;
        }
        catch (JsonException) { return null; }
    }
}
