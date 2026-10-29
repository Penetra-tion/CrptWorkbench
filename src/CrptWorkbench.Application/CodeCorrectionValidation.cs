using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

internal static class CodeCorrectionValidation
{
    internal static IReadOnlyList<string> Validate(CodeCorrectionRequest request, DateOnly today, bool includeCodes = true)
    {
        var errors = new List<string>();
        if (!CodeCorrectionRules.Supports(request.ProductGroup, request.Kind))
            errors.Add("Этот вид корректировки недоступен для выбранной товарной группы.");
        if (request.Codes.Count is < 1 or > 100_000) errors.Add("Укажите от 1 до 100 000 уникальных кодов.");
        if (request.Codes.Distinct(StringComparer.Ordinal).Count() != request.Codes.Count) errors.Add("Удалите повторяющиеся коды.");
        if (includeCodes)
            foreach (var code in request.Codes)
                if (CodeError(code) is { } error) { errors.Add($"{code}: {error}"); if (errors.Count >= 30) break; }
        var values = request.Values;
        if (values.Value.Any(char.IsControl) ||
            values.PermitDocuments?.Any(x => x.Number.Any(char.IsControl) || x.Date.Any(char.IsControl)) == true ||
            values.Licences?.Any(x => x.Number.Any(char.IsControl) || x.Date.Any(char.IsControl)) == true ||
            values.Expirations?.Any(x => x.Date.Any(char.IsControl)) == true)
            errors.Add("Значения полей не должны содержать управляющие символы или переводы строк.");
        switch (request.Kind)
        {
            case "productionDate":
                var minimum = request.ProductGroup is "toys" or "radio" or "gadgets" ? new DateOnly(1970, 1, 1) : today.AddYears(-20);
                CheckDate(values.Value, minimum, today.AddDays(1), "Дата производства", errors);
                break;
            case "expirationDate":
                CheckDate(values.Value, new(1999, 1, 1), new(2099, 12, 31), "Срок годности", errors);
                break;
            case "variableExpiration":
                if (values.Expirations is not { Count: > 0 }) errors.Add("Укажите условия хранения и сроки годности.");
                else
                {
                    if (values.Expirations.Select(x => x.Index).Distinct().Count() != values.Expirations.Count)
                        errors.Add("Индексы условий хранения не должны повторяться.");
                    foreach (var item in values.Expirations)
                    {
                        if (item.Index < 0) errors.Add("Индекс условия хранения должен быть неотрицательным.");
                        CheckDate(item.Date, new(1999, 1, 1), new(2099, 12, 31), "Срок годности", errors);
                    }
                }
                break;
            case "weight":
                if (!Regex.IsMatch(values.Value, "^[0-9]{1,6}$") || !int.TryParse(values.Value, out var weight) || weight < 1)
                    errors.Add("Вес должен быть целым числом от 1 до 999999 граммов.");
                break;
            case "tnved":
                if (!Regex.IsMatch(values.Value, "^[0-9]{10}$")) errors.Add("Код ТН ВЭД должен состоять из 10 цифр.");
                break;
            case "vsd":
                if (!Regex.IsMatch(values.Value, "^([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}|[A-F0-9]{4}(-[A-F0-9]{4}){7})$"))
                    errors.Add("Укажите идентификатор ВСД в формате UUID (строчные буквы) или восьми групп по четыре символа (прописные буквы).");
                break;
            case "emissionType":
                if (values.Value is not ("1" or "2")) errors.Add("Выберите тип эмиссии: произведён в РФ или ввезён в РФ.");
                break;
            case "partNumber":
                if (!Regex.IsMatch(values.Value, "^[0-9]{20}[0-9A-Za-z/.,-]{1,12}$") ||
                    !DateOnly.TryParseExact(values.Value.Length >= 20 ? values.Value.Substring(14, 6) : "", "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    errors.Add("Номер партии: 14 цифр GTIN, дата ГГММДД и 1–12 символов номера (латиница, цифры, / . , -).");
                break;
            case "permitDocs":
                if (values.Operation is not (0 or 1)) errors.Add("Выберите добавление или замену разрешительных документов.");
                if (values.PermitDocuments is not { Count: > 0 and <= 1000 }) errors.Add("Укажите от 1 до 1000 разрешительных документов.");
                else foreach (var item in values.PermitDocuments)
                {
                    if (!CodeCorrectionRules.AllowedPermitTypes(request.ProductGroup).Contains(item.Type)) errors.Add("Вид разрешительного документа недоступен для этой товарной группы.");
                    if (string.IsNullOrWhiteSpace(item.Number) || item.Number.Any(char.IsControl)) errors.Add("Укажите номер разрешительного документа без управляющих символов.");
                    CheckDate(item.Date, new(item.Type == 4 ? 1900 : 2000, 1, 1), today, "Дата разрешительного документа", errors);
                }
                break;
            case "licences":
                if (values.Operation is not (0 or 1)) errors.Add("Выберите добавление или замену лицензий.");
                if (values.Licences is not { Count: > 0 }) errors.Add("Укажите хотя бы одну лицензию.");
                else foreach (var item in values.Licences)
                {
                    if (string.IsNullOrWhiteSpace(item.Number)) errors.Add("Укажите номер лицензии.");
                    CheckDate(item.Date, new(1900, 1, 1), today.AddDays(1), "Дата лицензии", errors);
                }
                break;
        }
        return errors;
    }

    internal static string? CodeError(string code)
    {
        if (code.Length is < 18 or > 74) return "Длина кода должна быть от 18 до 74 символов.";
        if (!Regex.IsMatch(code, "^[A-Za-z0-9%&'\"()*+,_./:;<=>?!-]+$", RegexOptions.CultureInvariant) || MarkingCodeFormat.HasBracketedApplicationIdentifiers(code))
            return "Укажите нормализованный код без пробелов, управляющих символов и скобок вокруг 01/21.";
        return null;
    }

    internal static bool TryDate(string value, out DateOnly date) => DateOnly.TryParseExact(value,
        ["yyyy-MM-dd", "dd.MM.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static void CheckDate(string value, DateOnly min, DateOnly max, string label, List<string> errors)
    {
        if (!TryDate(value, out var date) || date < min || date > max)
            errors.Add($"{label}: допустима дата от {min:dd.MM.yyyy} до {max:dd.MM.yyyy}.");
    }

    internal static string BuildJson(string inn, CodeCorrectionRequest request)
    {
        var fields = new Dictionary<string, object> { ["code"] = request.Codes };
        var values = request.Values;
        switch (request.Kind)
        {
            case "permitDocs":
                fields["permitDocsOperation"] = values.Operation;
                fields["permitDocs"] = values.PermitDocuments!.Select(x => new { permitDocType = x.Type, permitDocNumber = x.Number, permitDocDate = FormatDate(x.Date) }).ToArray();
                break;
            case "licences":
                fields["licencesOperation"] = values.Operation;
                fields["licences"] = values.Licences!.Select(x => new { licence_number = x.Number, licence_date = FormatDate(x.Date) }).ToArray();
                break;
            case "variableExpiration":
                fields[request.Kind] = values.Expirations!.ToDictionary(x => x.Index.ToString(CultureInfo.InvariantCulture), x =>
                {
                    TryDate(x.Date, out var date);
                    return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
                });
                break;
            case "weight": case "emissionType": fields[request.Kind] = int.Parse(values.Value, CultureInfo.InvariantCulture); break;
            case "productionDate": case "expirationDate": fields[request.Kind] = FormatDate(values.Value); break;
            default: fields[request.Kind] = values.Value; break;
        }
        return JsonSerializer.Serialize(new { participantInn = inn, codes = new[] { fields } });
    }

    private static string FormatDate(string value)
    {
        TryDate(value, out var date);
        return date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }
}
