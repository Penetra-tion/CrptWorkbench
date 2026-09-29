using System.Text.Json;
using System.Text.RegularExpressions;


namespace CrptWorkbench.Domain;

public sealed record PermitDocumentRequest(string Number);

public sealed record PermitDocumentItem(string Type, string Number, string Country, string? Status,
    string? RegisteredAt, string? ExpiresAt, string? Product, string? Manufacturer,
    string? Applicant, string? TechnicalRegulations, string SourceName, string SourceUrl);

public sealed record PermitDocumentResult(string Number, string State, DateTimeOffset CheckedAt,
    IReadOnlyList<PermitDocumentItem> Items, string? Notice, string ManualUrl)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PermitDocumentResult? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<PermitDocumentResult>(json, Options);
            return result is { Number.Length: > 0, Items: not null, ManualUrl.Length: > 0 } &&
                result.State is "found" or "not_found" or "multiple" or "unavailable" ? result : null;
        }
        catch (JsonException) { return null; }
    }
}

public enum PermitNumberKind { Certificate, Declaration, StateRegistration, Ambiguous }

public sealed record PermitNumber(string Value, PermitNumberKind Kind, string Country);

public static class PermitDocumentNumbers
{
    private const string Countries = "RU|BY|KZ|AM|KG";
    private const string RussianTail = @"[A-Z]{2}\.[А-ЯЁ0-9]{4}\.[АВ]\.\d{5}(?:/\d{2})?";
    private static readonly Regex RussianCertificate = new(@"\A(?:(?:ЕАЭС|РОСС|ТС|ВП)\s+)?RU\s+С-" + RussianTail + @"\z", RegexOptions.CultureInvariant);
    private static readonly Regex RussianDeclaration = new(@"\A(?:(?:ЕАЭС|РОСС|ТС|ВП)\s+(?:N\s+)?)?RU\s+Д-" + RussianTail + @"\z", RegexOptions.CultureInvariant);
    private static readonly Regex Registration = new(@"\A(?<country>" + Countries + @")\.(?<region>\d{2})\.[А-ЯЁ0-9]{2}\.\d{2}\.\d{3}\.[ЕеEeRr]\.(?<serial>\d{6})\.(?<month>\d{2})\.\d{2}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Foreign = new(@"\A(?:ЕАЭС|ТС)\s+(?:(?:N|№)\s+)?(?<country>" + Countries + @")(?<tail>[A-Za-zА-Яа-яЁё0-9 ._/-]{5,30})\z", RegexOptions.CultureInvariant);

    public static PermitNumber Parse(string? raw)
    {
        var value = raw?.Replace('\u00a0', ' ').Trim() ?? "";
        if (value.Length is < 12 or > 100 || value.Any(char.IsControl))
            throw new ArgumentException("Укажите номер разрешительного документа в допустимом формате.");
        if (RussianCertificate.IsMatch(value)) return new(value, PermitNumberKind.Certificate, "RU");
        if (RussianDeclaration.IsMatch(value)) return new(value, PermitNumberKind.Declaration, "RU");
        var registration = Registration.Match(value);
        if (registration.Success && int.Parse(registration.Groups["region"].Value) is >= 1 and <= 99 &&
            int.Parse(registration.Groups["serial"].Value) is >= 1 and <= 999999 &&
            int.Parse(registration.Groups["month"].Value) is >= 1 and <= 12)
            return new(value, PermitNumberKind.StateRegistration, registration.Groups["country"].Value);
        var foreign = Foreign.Match(value);
        if (foreign.Success) return new(value, PermitNumberKind.Ambiguous, foreign.Groups["country"].Value);
        throw new ArgumentException("Номер не соответствует формату сертификата, декларации или СГР из справочника.");
    }
}
