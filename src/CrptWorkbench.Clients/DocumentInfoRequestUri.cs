using System.Text.RegularExpressions;

namespace CrptWorkbench.Clients;

internal static partial class DocumentInfoRequestUri
{
    public static Uri Build(string relativeUrl, string host)
    {
        if (Uri.TryCreate(relativeUrl, UriKind.Absolute, out _) || !relativeUrl.StartsWith("doc/", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid document info request.");
        var question = relativeUrl.IndexOf('?');
        if (question < 0) throw new InvalidOperationException("Invalid document info request.");
        var path = relativeUrl[..question];
        var query = relativeUrl[(question + 1)..];
        var segments = path.Split('/');
        if (segments.Length != 3 || segments[0] != "doc" || segments[2] != "info" ||
            segments[1].Length is < 1 or > 768 || segments[1] is "." or ".." ||
            !CanonicalDocumentId().IsMatch(segments[1]))
            throw new InvalidOperationException("Invalid document info request.");
        var id = Uri.UnescapeDataString(segments[1]);
        if (id.Length is < 1 or > 256 || id.Any(char.IsControl) || id.IndexOfAny(['/','\\','%','?','#']) >= 0 ||
            id is "." or ".." || Uri.EscapeDataString(id) != segments[1])
            throw new InvalidOperationException("Invalid document info request.");
        var parameters = query.Split('&');
        if (parameters.Length is < 2 or > 3 || parameters[0] != "body=false" || parameters[1] != "content=false" ||
            (parameters.Length == 3 && !ProductGroup().IsMatch(parameters[2])))
            throw new InvalidOperationException("Invalid document info request.");
        return new Uri($"https://{host}/api/v4/true-api/{relativeUrl}", UriKind.Absolute);
    }

    [GeneratedRegex("^[A-Za-z0-9._~-]+(?:%[0-9A-F]{2}[A-Za-z0-9._~-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalDocumentId();
    [GeneratedRegex("^pg=[a-z0-9_]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductGroup();
}
