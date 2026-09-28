using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

internal static class CodeHistoryRequestUri
{
    private const string Prefix = CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/cises/history?cis=";

    public static bool IsAllowed(string url)
    {
        if (!url.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var encoded = url[Prefix.Length..];
        if (encoded.Length == 0 || encoded.Contains('#')) return false;
        var code = Uri.UnescapeDataString(encoded);
        return code.Length is >= 18 and <= 74 && !code.Any(char.IsControl) &&
            !code.Contains("(01)", StringComparison.Ordinal) && !code.Contains("(21)", StringComparison.Ordinal) &&
            Uri.EscapeDataString(code) == encoded;
    }

    public static bool IsAllowed(Uri uri) => IsAllowed(uri.AbsoluteUri);
}
