using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Infrastructure;

public sealed partial class SensitiveDataMasker : ISensitiveDataMasker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string? MaskHeaders(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(value);
            if (headers is null) return value;
            foreach (var key in headers.Keys.Where(IsSensitive).ToArray())
            {
                headers[key] = headers[key].StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? "Bearer ********"
                    : "********";
            }

            return JsonSerializer.Serialize(headers, JsonOptions);
        }
        catch (JsonException)
        {
            return SecretAssignmentRegex().Replace(value, "$1********");
        }
    }

    public string? MaskBody(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            var node = JsonNode.Parse(value);
            MaskNode(node);
            return node?.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return SecretAssignmentRegex().Replace(value, "$1********");
        }
    }

    private static void MaskNode(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (IsSensitive(property.Key)) jsonObject[property.Key] = "********";
                else MaskNode(property.Value);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray) MaskNode(child);
        }
    }

    private static bool IsSensitive(string key) => SensitiveKeyRegex().IsMatch(key);

    [GeneratedRegex("authorization|api[-_ ]?key|token|password|secret|signature|set-cookie|cookie", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyRegex();

    [GeneratedRegex("((?:authorization|api[-_ ]?key|token|password|secret|signature|set-cookie|cookie)\\s*[:=]\\s*)(?:Bearer\\s+)?[^,;\\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();
}
