using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

internal static class SuzUtilisationRequestRules
{
    private static readonly HashSet<string> Groups = new("tobacco milk otp water beer ncp bio antiseptic nabeer wheelchairs softdrinks seafood petfood toys titan radio opticfiber vegetableoil chemistry conserve vetpharma bicycle construction fire books heater grocery cableraw autofluids polymer sweets carparts nicotindev gadgets meat vetbio homeware industrial fertilizers frozen furslp pyrotechnics".Split(' '), StringComparer.Ordinal);
    public static bool IsReport(Uri uri) => SuzRequestRules.IsOrigin(uri, "suzgrid.crpt.ru") && uri.AbsolutePath == "/api/v3/report/info" && Pair(uri, "reportId");
    public static bool IsCreation(Uri uri) => SuzRequestRules.IsOrigin(uri, "suzgrid.crpt.ru") && uri.AbsolutePath == "/api/v3/utilisation" && uri.Query.StartsWith("?omsId=", StringComparison.Ordinal) && SuzRequestRules.IsGuid(uri.Query[7..]);
    private static bool Pair(Uri uri, string key)
    {
        var parts = uri.Query.TrimStart('?').Split('&');
        return parts.Length == 2 && parts[0].StartsWith("omsId=", StringComparison.Ordinal) && SuzRequestRules.IsGuid(parts[0][6..]) && parts[1].StartsWith(key + "=", StringComparison.Ordinal) && SuzRequestRules.IsGuid(parts[1][(key.Length + 1)..]);
    }
    public static bool ValidBody(string? body)
    {
        try
        {
            using var document = JsonDocument.Parse(body ?? "");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("productGroup" or "sntins" or "utilisationType" or "attributes")) || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1)) return false;
            if (!root.TryGetProperty("productGroup", out var groupValue) || groupValue.ValueKind != JsonValueKind.String || !Groups.Contains(groupValue.GetString()!)) return false;
            var group = groupValue.GetString();
            if (!root.TryGetProperty("sntins", out var codes) || codes.ValueKind != JsonValueKind.Array || codes.GetArrayLength() is < 1 or > 30000) return false;
            foreach (var code in codes.EnumerateArray())
            {
                if (code.ValueKind != JsonValueKind.String) return false;
                var value = code.GetString()!;
                if (value.Length < 20 || value.Any(c => char.IsControl(c) && c is not ('\u001d' or '\u001e'))) return false;
                var fixedCode = group is "tobacco" or "otp" or "ncp" && !value.Contains('\u001d') && value.Length >= 29 && value[..14].All(char.IsAsciiDigit);
                if (!fixedCode && !value.Contains("\u001d93", StringComparison.Ordinal) && !(value.Contains("\u001d91", StringComparison.Ordinal) && value.Contains("\u001d92", StringComparison.Ordinal))) return false;
            }
            var scenario = "UTILISATION";
            if (root.TryGetProperty("utilisationType", out var type)) { if (type.ValueKind != JsonValueKind.String) return false; scenario = type.GetString()!; }
            if (!(scenario == "UTILISATION" || scenario == "RESORT" && group == "beer" || scenario == "SPLITMARK" && group is "toys" or "chemistry" or "radio" || scenario == "DIVISIONMARK" && group == "radio" || scenario == "REMARK" && group == "chemistry" && DateTimeOffset.UtcNow >= new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero))) return false;
            if (root.TryGetProperty("attributes", out var attributes) && !ValidAttributes(attributes)) return false;
            return true;
        }
        catch (JsonException) { return false; }
    }
    private static bool ValidAttributes(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object) return false;
        var allowed = new HashSet<string>("productionDate expirationDate expDate expDate72 expDateConditions usedInProduction batchNumber seriesNumber productionOrderId brandcode sourceReportId productionLineId kpp fiasId participantId alcoholVolume documentNumber documentDate documentSource vetisGuid".Split(' '), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in attributes.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) return false;
            if (property.Name == "usedInProduction")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value) || value is not (0 or 1)) return false;
            }
            else if (property.Name == "expDateConditions")
            {
                if (property.Value.ValueKind != JsonValueKind.Object) return false;
                var indices = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in property.Value.EnumerateObject())
                    if (entry.Name.Length == 0 || !entry.Name.All(char.IsAsciiDigit) || !indices.Add(entry.Name) || entry.Value.ValueKind != JsonValueKind.String || entry.Value.GetString()!.Any(char.IsControl)) return false;
            }
            else if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Any(char.IsControl)) return false;
        }
        return true;
    }
    public static string SafeResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var safe = new Dictionary<string, object?>();
            foreach (var name in new[] { "reportId", "resultDocId", "omsId" })
                if (document.RootElement.TryGetProperty(name, out var value)) safe[name] = value.ValueKind == JsonValueKind.String && SuzRequestRules.IsGuid(value.GetString()) ? value.GetString() : "invalid";
            foreach (var name in new[] { "status", "reportStatus" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is "SUCCESS" or "FAILED" or "IN_PROGRESS" or "PARTIALLY" or "PENDING" or "REJECTED" or "DRAFT" or "READY_TO_SEND" or "SENT" or "CHECK") safe[name] = value.GetString();
            return JsonSerializer.Serialize(safe);
        }
        catch (JsonException) { return "{}"; }
    }
}
