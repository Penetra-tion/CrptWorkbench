using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

// Packages: True API v733.0, sections 4.1.1 and 4.1.2.1.
// Corrections: reviewed v733.0 snapshot, section 4.1.17.
internal static class PackageCreationRequest
{
    // Stable order also encodes non-sensitive mock metadata in document identifiers.
    internal static readonly string[] Groups =
    [
        "carparts", "antiseptic", "grocery", "nabeer", "bio", "bicycle", "vetpharma", "perfumery",
        "toys", "cableraw", "conserve", "petfood", "wheelchairs", "milk", "seafood", "autofluids", "shoes",
        "opticfiber", "heater", "chemistry", "books", "beer", "fire", "polymer", "lp", "radio",
        "vegetableoil", "alcohol", "sweets", "softdrinks", "construction", "water", "electronics", "tires"
    ];

    internal static bool IsCreation(string operation) =>
        operation is "true-api.package-transformation" or "true-api.package-aggregation" or "true-api.code-correction";

    internal static bool IsAllowedGroup(string group) => Groups.Contains(group, StringComparer.Ordinal) || IsCorrectionGroup(group);
    private static bool IsCorrectionGroup(string group) => CodeCorrectionRules.Groups.Any(x => x.Code == group);

    internal static (string Group, string Type, string Document) Validate(IntegrationRequest request)
    {
        const string prefix = "lk/documents/create?pg=";
        if (!IsCreation(request.OperationId) || request.Method != "POST" ||
            !request.Url.StartsWith(prefix, StringComparison.Ordinal) ||
            !(request.OperationId == "true-api.code-correction" ? IsCorrectionGroup(request.Url[prefix.Length..]) : Groups.Contains(request.Url[prefix.Length..], StringComparer.Ordinal)) || string.IsNullOrWhiteSpace(request.Body))
            throw new InvalidOperationException("Invalid package document creation request.");

        var type = request.OperationId switch
        {
            "true-api.package-aggregation" => "AGGREGATION_DOCUMENT",
            "true-api.code-correction" => "CIS_INFORMATION_CHANGE",
            _ => "REAGGREGATION_DOCUMENT"
        };
        try
        {
            using var envelope = JsonDocument.Parse(request.Body);
            var root = envelope.RootElement;
            if (request.OperationId == "true-api.package-aggregation" &&
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var requestedType) &&
                requestedType.GetString() == "SETS_AGGREGATION")
            {
                if (!PackageSetRules.SupportsGroup(request.Url[prefix.Length..]))
                    throw new InvalidOperationException("Product group does not support set formation.");
                type = "SETS_AGGREGATION";
            }
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
                root.GetProperty("type").GetString() != type ||
                root.GetProperty("document_format").GetString() != "MANUAL" ||
                string.IsNullOrWhiteSpace(root.GetProperty("signature").GetString()))
                throw new InvalidOperationException("Invalid signed package document envelope.");
            var encoded = root.GetProperty("product_document").GetString();
            if (string.IsNullOrWhiteSpace(encoded)) throw new FormatException();
            var bytes = Convert.FromBase64String(encoded);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException();
            if (type == "CIS_INFORMATION_CHANGE") ValidateCorrection(document.RootElement, request.Url[prefix.Length..]);
            return (request.Url[prefix.Length..], type, document.RootElement.GetRawText());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException("Invalid signed package document envelope.", exception);
        }
    }

    private static void ValidateCorrection(JsonElement document, string group)
    {
        var rootProperties = document.EnumerateObject().Select(x => x.Name).ToArray();
        if (rootProperties.Length != 2 || !rootProperties.Contains("participantInn") || !rootProperties.Contains("codes"))
            throw new FormatException();
        if (document.GetProperty("participantInn").ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(document.GetProperty("participantInn").GetString())) throw new FormatException();
        var entries = document.GetProperty("codes");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 1) throw new FormatException();
        var entry = entries[0];
        var codes = entry.GetProperty("code");
        if (codes.ValueKind != JsonValueKind.Array || codes.GetArrayLength() is < 1 or > 100000 ||
            codes.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString()))) throw new FormatException();
        var properties = entry.EnumerateObject().Select(x => x.Name).ToArray();
        if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length) throw new FormatException();
        var fields = properties.Where(x => x is not ("code" or "permitDocsOperation" or "licencesOperation")).ToArray();
        if (fields.Length != 1 || !CodeCorrectionRules.Supports(group, fields[0])) throw new FormatException();
        if (properties.Contains("permitDocsOperation") != (fields[0] == "permitDocs") ||
            properties.Contains("licencesOperation") != (fields[0] == "licences")) throw new FormatException();
    }
}
