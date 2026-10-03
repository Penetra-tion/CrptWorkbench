using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrptWorkbench.Application;

public sealed record SuzUtilisationRow(string Code, IReadOnlyDictionary<string, string> IndividualValues);
public sealed record SuzUtilisationRequest(string ProductGroup, string PackageType, string UtilisationType,
    IReadOnlyDictionary<string, string> CommonValues, IReadOnlyList<SuzUtilisationRow> Rows);
public sealed record SuzUtilisationInputError(int? RowNumber, string Field, string Message);
public sealed record SuzUtilisationPreparedReport(string Json, int CodeCount, string? Gtin);
public sealed record SuzUtilisationPreparation(string ProductGroup, string PackageType, string Scenario,
    IReadOnlyList<SuzUtilisationPreparedReport> Reports, int CodeCount, IReadOnlyList<SuzUtilisationInputError> Errors)
{
    public bool IsValid => Errors.Count == 0 && Reports.Count > 0;
}

public static partial class SuzUtilisationInput
{
    public const int ReportCodeLimit = 30000;
    private const char Gs = '\u001d';
    private const char Rs = '\u001e';
    public static IReadOnlyList<SuzUtilisationRow> ParseText(string text)
    {
        var lines = text.TrimStart('\ufeff').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Any(x => x.Contains('\t'))) throw new FormatException("TSV не поддерживается. Укажите один код в строке.");
        return Codes(lines);
    }

    public static IReadOnlyList<SuzUtilisationRow> ParseCsv(string text)
    {
        text = text.TrimStart('\ufeff');
        var delimiter = CsvDelimiter(text);
        var values = new List<string>();
        var field = new StringBuilder();
        var rows = new List<string>();
        var quoted = false;
        var afterQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (quoted) { quoted = false; afterQuote = true; }
                else if (field.Length == 0) quoted = true;
                else throw new FormatException("Неверные кавычки в CSV.");
            }
            else if (!quoted && c == delimiter) { values.Add(field.ToString()); field.Clear(); afterQuote = false; }
            else if (!quoted && c is '\r' or '\n')
            {
                values.Add(field.ToString()); field.Clear(); rows.Add(values[0]); values.Clear(); afterQuote = false;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (!quoted && c == '\t') throw new FormatException("TSV не поддерживается.");
            else if (afterQuote) throw new FormatException("Неверные кавычки в CSV.");
            else field.Append(c);
        }
        if (quoted) throw new FormatException("Незакрытые кавычки в CSV.");
        if (field.Length > 0 || values.Count > 0) { values.Add(field.ToString()); rows.Add(values[0]); }
        return Codes(rows);
    }

    private static char CsvDelimiter(string text)
    {
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is ',' or ';') return c;
        }
        return ',';
    }

    public static IReadOnlyList<SuzUtilisationRow> Codes(IEnumerable<string> codes)
    {
        var rows = new List<SuzUtilisationRow>();
        foreach (var raw in codes)
        {
            var value = raw.Trim(' ', '\t', '\r', '\n');
            if (value.Length == 0 || rows.Count == 0 && value == "code") continue;
            rows.Add(new(DecodeSeparators(value), new Dictionary<string, string>(StringComparer.Ordinal)));
        }
        return rows;
    }

    public static string TemplateCsv() => "code\n";

    public static SuzUtilisationPreparation Prepare(SuzUtilisationRequest request, DateTimeOffset now)
    {
        var errors = new List<SuzUtilisationInputError>();
        void Error(string field, string message, int? row = null) => errors.Add(new(row, field, message));
        var fields = SuzUtilisationRules.Fields(request.ProductGroup, request.PackageType, request.UtilisationType);
        if (!SuzUtilisationRules.Groups.Any(x => x.Code == request.ProductGroup && !x.Automatic)) Error("productGroup", "Ручное нанесение недоступно для выбранной товарной группы.");
        if (!SuzUtilisationRules.PackageTypes(request.ProductGroup).Any(x => x.Code == request.PackageType)) Error("packageType", "Тип упаковки недоступен для выбранной группы.");
        if (!SuzUtilisationRules.Scenarios(request.ProductGroup).Any(x => x.Code == request.UtilisationType)) Error("utilisationType", "Сценарий недоступен для выбранной группы.");
        if (request.UtilisationType == "REMARK" && now.Date < new DateTime(2026, 8, 27)) Error("utilisationType", "Перемаркировка доступна с 27 августа 2026 года.");
        var common = request.CommonValues.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key, x => x.Value.Trim(), StringComparer.Ordinal);
        if (fields.Any(x => x.Code == "usedInProduction") && !common.ContainsKey("usedInProduction")) common["usedInProduction"] = "1";
        foreach (var key in common.Keys) if (!fields.Any(x => x.Code == key)) Error(key, "Атрибут несовместим с выбранными параметрами.");
        foreach (var field in fields.Where(x => x.Required)) if (!common.ContainsKey(field.Code)) Error(field.Code, $"Заполните поле «{field.Name}».");
        foreach (var field in fields.Where(x => common.ContainsKey(x.Code))) ValidateField(field, common[field.Code], now, Error);
        if (common.TryGetValue("productionDate", out var tobaccoDate) && request.ProductGroup is "tobacco" or "otp" or "ncp")
        {
            if (!Regex.IsMatch(tobaccoDate, "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[+-][0-9]{4}$") ||
                !DateTimeOffset.TryParse(tobaccoDate.Insert(tobaccoDate.Length - 2, ":"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var tobaccoProduction)) Error("productionDate", "Для табака дата должна иметь формат yyyy-MM-ddTHH:mm:ss±hhmm.");
            else if (tobaccoProduction > now) Error("productionDate", "Дата производства не должна быть позднее текущего момента.");
        }
        ValidateConditions(request, common, now, Error);
        var codes = new List<(string Code, string Gtin)>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var factorySerials = new HashSet<string>(StringComparer.Ordinal);
        var allowedIndividual = SuzUtilisationRules.IndividualFields(request.ProductGroup, request.PackageType).Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        for (var rowIndex = 0; rowIndex < request.Rows.Count; rowIndex++)
        {
            var row = request.Rows[rowIndex]; var number = rowIndex + 1;
            var code = DecodeSeparators(row.Code.Trim(' ', '\t'));
            var pair = code.Split(Rs);
            var special = request.UtilisationType != "UTILISATION";
            if (pair.Length != (special ? 2 : 1)) { Error("code", special ? "Нужны два кода, разделённые ASCII 30." : "Для обычного нанесения нужен один полный код.", number); continue; }
            if (!TryCode(pair[0], request.ProductGroup, request.PackageType, true, out var parsed)) { Error("code", "Полный код не соответствует шаблону товарной группы.", number); continue; }
            if (special)
            {
                if (!TryCode(pair[1], request.ProductGroup, request.PackageType, request.UtilisationType == "RESORT", out var previous))
                    Error("code", "Второй код не соответствует формату выбранного сценария.", number);
                else if (previous.Gtin != parsed.Gtin) Error("code", "Коды выбранного сценария должны содержать одинаковый GTIN.", number);
                if (row.IndividualValues.Any(x => !string.IsNullOrWhiteSpace(x.Value))) Error("code", "Индивидуальные данные не добавляются к специальным сценариям.", number);
            }
            var optional = new Dictionary<string, string>(parsed.Individual, StringComparer.Ordinal);
            foreach (var (key, raw) in row.IndividualValues.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
            {
                if (!allowedIndividual.Contains(key)) { Error(key, "Индивидуальное поле несовместимо с товарной группой или упаковкой.", number); continue; }
                var value = raw.Trim();
                if (key == "97")
                {
                    if (value.Length is < 1 or > 30 || value.Any(char.IsControl)) { Error(key, "Серийный номер должен содержать от 1 до 30 печатных символов.", number); continue; }
                    value = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
                }
                else if (!ValidAi(key, value)) { Error(key, "Значение не соответствует длине и числовому формату AI.", number); continue; }
                if (optional.TryGetValue(key, out var existing) && existing != value) { Error(key, "Значение противоречит уже встроенному в код.", number); continue; }
                optional[key] = value;
            }
            foreach (var key in optional.Keys) if (!allowedIndividual.Contains(key)) Error(key, "Встроенный AI запрещён для выбранной группы или упаковки.", number);
            if (optional.Count > 1) Error("code", "Код может содержать только одну переменную характеристику.", number);
            if (optional.ContainsKey("3103") && request.ProductGroup is "seafood" or "petfood" or "sweets" or "meat" or "frozen" && common.GetValueOrDefault("_variableWeight") != "1")
                Error("3103", "Вес разрешён только при признаке переменного веса в карточке товара.", number);
            if (request.ProductGroup == "wheelchairs" && request.PackageType == "UNIT" && !common.ContainsKey("batchNumber") && !common.ContainsKey("seriesNumber") && !optional.ContainsKey("97"))
                Error("97", "Укажите партию, серию или заводской серийный номер для каждого кода.", number);
            var full = special ? code : parsed.BaseCode + string.Concat(optional.Select(x => Gs + x.Key + x.Value));
            var identity = special ? parsed.Identity : parsed.BaseCode;
            if (seen.TryGetValue(identity, out var duplicate))
            {
                if (duplicate != full) Error("code", "Один код указан с противоречащими индивидуальными данными.", number);
                continue;
            }
            seen.Add(identity, full);
            if (optional.TryGetValue("97", out var serial) && !factorySerials.Add(parsed.Gtin + "|" + serial)) Error("97", "Заводской серийный номер должен быть уникален в пределах GTIN.", number);
            if (request.ProductGroup == "milk" && common.TryGetValue("batchNumber", out var batch) && !batch.StartsWith(parsed.Gtin, StringComparison.Ordinal)) Error("batchNumber", "GTIN номера партии должен совпадать с GTIN кода.", number);
            codes.Add((full, parsed.Gtin));
        }
        if (request.Rows.Count == 0) Error("code", "Добавьте хотя бы один полный код маркировки.");
        var reports = new List<SuzUtilisationPreparedReport>();
        if (errors.Count == 0)
        {
            var attributes = BuildAttributes(common);
            var partitions = common.ContainsKey("expDateConditions")
                ? codes.GroupBy(x => x.Gtin).Select(x => x.ToArray()) : new[] { codes.ToArray() }.AsEnumerable();
            foreach (var partition in partitions) foreach (var chunk in partition.Chunk(ReportCodeLimit))
            {
                var body = new Dictionary<string, object> { ["productGroup"] = request.ProductGroup, ["utilisationType"] = request.UtilisationType, ["sntins"] = chunk.Select(x => x.Code).ToArray() };
                if (attributes.Count > 0) body["attributes"] = attributes;
                reports.Add(new(JsonSerializer.Serialize(body), chunk.Length, chunk.Select(x => x.Gtin).Distinct().Count() == 1 ? chunk[0].Gtin : null));
            }
        }
        return new(request.ProductGroup, request.PackageType, request.UtilisationType, reports, codes.Count, errors);
    }

    private static Dictionary<string, object> BuildAttributes(Dictionary<string, string> common)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in common.Where(x => !x.Key.StartsWith('_')))
            result[key] = key == "expDateConditions" ? JsonSerializer.Deserialize<Dictionary<string, string>>(value)!
                : key == "usedInProduction" ? int.Parse(value, CultureInfo.InvariantCulture) : value;
        return result;
    }

    private static void ValidateField(SuzUtilisationField field, string value, DateTimeOffset now, Action<string, string, int?> error)
    {
        void Fail(string message) => error(field.Code, message, null);
        if (value.Any(char.IsControl)) { Fail("Атрибут не должен содержать управляющие символы."); return; }
        if (field.Options is not null && !field.Options.Any(x => x.Code == value)) Fail("Выберите допустимое значение.");
        if (field.Code is "fiasId" or "sourceReportId" or "vetisGuid" && !Guid.TryParseExact(value, "D", out _)) Fail("Требуется GUID.");
        if (field.Code == "participantId" && !Regex.IsMatch(value, "^[0-9]{10}([0-9]{2})?$")) Fail("ИНН должен содержать 10 или 12 цифр.");
        if (field.Code == "kpp" && !Regex.IsMatch(value, "^[0-9]{9}$")) Fail("КПП должен содержать 9 цифр.");
        if (field.Code == "alcoholVolume" && !Regex.IsMatch(value, "^[0-9]{1,2}(\\.[0-9])?$")) Fail("Укажите 1–2 цифры и не более одного знака после точки.");
        if (field.Code is "documentNumber" or "documentSource" && value.Length > 255) Fail("Не более 255 символов.");
        if (field.Code is "brandcode" or "productionOrderId" or "productionLineId" && value.Length > 256) Fail("Не более 256 символов.");
        if (field.InputType is "date" or "datetime-local")
        {
            var format = field.Code == "expDate72" ? "yyyy-MM-dd'T'HH:mm" : "yyyy-MM-dd";
            if (!DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                Fail("Неверный формат даты.");
            }
            else if (field.Code == "productionDate" && date.Date > now.Date) Fail("Дата производства не должна быть позднее текущей даты.");
        }
        if (field.Code == "expDateConditions")
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() == 0) { Fail("Добавьте условия хранения и сроки годности."); return; }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var condition in document.RootElement.EnumerateObject())
                    if (!Regex.IsMatch(condition.Name, "^[0-9]+$") || !seen.Add(condition.Name) || condition.Value.ValueKind != JsonValueKind.String ||
                        !DateTime.TryParseExact(condition.Value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry) || expiry.Date < now.Date)
                        Fail("Индексы должны быть уникальными числами; даты — yyyy-MM-dd, не ранее текущей.");
            }
            catch (JsonException) { Fail("Неверный формат условий хранения."); }
        }
    }

    private static void ValidateConditions(SuzUtilisationRequest request, Dictionary<string, string> values, DateTimeOffset now, Action<string, string, int?> error)
    {
        bool Has(string code) => values.ContainsKey(code);
        void Require(string code, string message) { if (!Has(code)) error(code, message, null); }
        var unit = request.PackageType == "UNIT"; var ordinary = request.UtilisationType == "UTILISATION";
        var expiryFields = new[] { "expirationDate", "expDate", "expDate72", "expDateConditions" }.Where(Has).ToArray();
        if (expiryFields.Length > 1) error("expirationDate", "Выберите только один способ указания срока годности.", null);
        if (unit && ordinary && request.ProductGroup == "milk" && expiryFields.Length == 0) error("expDate", "Укажите срок годности одним из доступных способов.", null);
        if (unit && ordinary && request.ProductGroup != "milk" && SuzUtilisationRules.VariableExpiry.Contains(request.ProductGroup) && expiryFields.Length == 0) error("expirationDate", "Укажите срок годности одним из доступных способов.", null);
        if (unit && ordinary && SuzUtilisationRules.ConditionalExpiry.Contains(request.ProductGroup) && values.GetValueOrDefault("_hasShelfLife") == "1") Require("expirationDate", "Укажите срок годности из карточки товара.");
        if (values.GetValueOrDefault("_cementWarranty") == "1")
        {
            foreach (var code in new[] { "productionDate", "expirationDate" }) if (Has(code)) error(code, "Для ТН ВЭД 2523 с гарантийным сроком атрибут запрещён.", null);
        }
        else if (unit && ordinary && request.ProductGroup == "construction") { Require("productionDate", "Укажите дату производства."); Require("expirationDate", "Укажите дату срока годности."); }
        if (values.TryGetValue("_participantKind", out var kind)) Require(kind == "LEGAL" ? "kpp" : "fiasId", "Укажите место деятельности для выбранного типа участника.");
        if (request.ProductGroup == "beer" && ordinary && values.GetValueOrDefault("_producedInRussia") == "1") { Require("documentNumber", "Укажите первичный учётный документ."); Require("documentDate", "Укажите дату первичного учётного документа."); }
        DateTime? production = null;
        if (values.TryGetValue("productionDate", out var productionText) && DateTime.TryParse(productionText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p)) production = p;
        DateTime? expiryDate = null;
        foreach (var code in expiryFields.Where(x => x != "expDateConditions"))
            if (DateTime.TryParse(values[code], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                expiryDate = date;
                if (production is not null && date < production) error(code, "Срок годности не должен быть раньше даты производства.", null);
                if (code == "expirationDate" && SuzUtilisationRules.Set("antiseptic seafood petfood vegetableoil chemistry conserve vetpharma toys construction fire grocery autofluids sweets meat vetbio fertilizers frozen pyrotechnics").Contains(request.ProductGroup) && date.Date <= now.Date)
                    error(code, "Срок годности должен быть позже текущей даты.", null);
            }
        if (request.ProductGroup == "beer" && ordinary && unit && production is not null && production.Value.Date < now.Date.AddDays(-1)) error("productionDate", "Отчёт по пиву подаётся не позднее одного календарного дня со дня производства.", null);
        if (request.ProductGroup == "milk" && unit)
        {
            var days = expiryDate is not null && production is not null ? (expiryDate.Value - production.Value).TotalDays : double.NaN;
            var dayValues = new List<double> { days };
            if (production is not null && values.TryGetValue("expDateConditions", out var conditions))
            {
                try
                {
                    using var document = JsonDocument.Parse(conditions);
                    if (document.RootElement.ValueKind == JsonValueKind.Object) foreach (var property in document.RootElement.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.String && DateTime.TryParseExact(property.Value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var conditionDate)) dayValues.Add((conditionDate - production.Value).TotalDays);
                }
                catch (JsonException) { /* Field validation already records the safe error. */ }
            }
            if (now.Date >= new DateTime(2026, 11, 1) && dayValues.Any(x => x > 40) || now.Date >= new DateTime(2027, 3, 1) && dayValues.Any(x => x < 40)) Require("batchNumber", "По календарным требованиям СУЗ необходим номер партии.");
            if (values.TryGetValue("batchNumber", out var batch))
            {
                if (!Regex.IsMatch(batch, "^[0-9]{20}[A-Za-z0-9/.,-]{1,12}$")) error("batchNumber", "Номер партии: GTIN + ГГММДД + 1–12 допустимых символов.", null);
                else if (!DateTime.TryParseExact(batch.Substring(14, 6), "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var batchDate) || production is not null && batchDate.Date != production.Value.Date) error("batchNumber", "Дата в номере партии должна совпадать с датой производства.", null);
            }
        }
        if (request.ProductGroup == "wheelchairs") foreach (var key in new[] { "batchNumber", "seriesNumber" })
            if (values.TryGetValue(key, out var value) && !Regex.IsMatch(value, "^[A-Za-zА-Яа-яЁё0-9/ .,\\-]{1,30}$")) error(key, "От 1 до 30 букв, цифр, пробелов или /.,-.", null);
    }

    private sealed record ParsedCode(string BaseCode, string Gtin, string Identity, IReadOnlyDictionary<string, string> Individual);
    private static bool TryCode(string code, string group, string package, bool full, out ParsedCode parsed)
    {
        parsed = new("", "", "", new Dictionary<string, string>());
        if (code.Any(c => char.IsControl(c) && c != Gs)) return false;
        if (group is "tobacco" or "otp" or "ncp" && package == "UNIT" && Regex.IsMatch(code, "^[0-9]{14}[!-~]{15}$"))
        { parsed = new(code, code[..14], code[..25], new Dictionary<string, string>()); return full; }
        if (package == "UNIT" && group is "tobacco" or "ncp") return false;
        var pieces = code.Split(Gs);
        if (!Regex.IsMatch(pieces[0], "^01[0-9]{14}21[!-~]+$")) return false;
        var serialLength = pieces[0].Length - 18;
        var shortLengths = group switch
        {
            "milk" or "seafood" or "petfood" or "chemistry" or "conserve" or "heater" or "polymer" or "sweets" or "carparts" or "meat" or "vetbio" or "homeware" or "industrial" or "fertilizers" or "frozen" or "pyrotechnics" or "toys" or "fire" => new[] { 6 },
            "tobacco" or "otp" or "ncp" or "nabeer" => new[] { 7 },
            "beer" => new[] { 7, 13 },
            "radio" or "gadgets" => new[] { 20 },
            _ => new[] { 13 }
        };
        var longLength = group is "seafood" or "petfood" or "chemistry" or "conserve" or "heater" or "polymer" ? 6 : group is "radio" or "gadgets" ? 20 : 13;
        if (!full)
        {
            if (pieces.Length != 1 || !shortLengths.Contains(serialLength) && serialLength != longLength) return false;
            parsed = new(code, code.Substring(2, 14), pieces[0], new Dictionary<string, string>()); return true;
        }
        var index = 1;
        if (group is "tobacco" or "otp" or "ncp")
        {
            if (serialLength != 7 || pieces.Length < 3 || !Regex.IsMatch(pieces[index++], "^8005[0-9]{6}$")) return false;
            if (group is "otp" or "ncp" && pieces[1] != "8005000000") return false;
        }
        if (index >= pieces.Length) return false;
        if (pieces[index].StartsWith("93", StringComparison.Ordinal))
        {
            if (group is "bicycle" or "wheelchairs" or "vetpharma" or "books" or "furslp" || !shortLengths.Contains(serialLength) || !Regex.IsMatch(pieces[index++], "^93[!-~]{4}$")) return false;
        }
        else
        {
            if (group is "milk" or "water" or "beer" or "nabeer" or "softdrinks" or "titan" or "tobacco" or "otp" or "ncp" || serialLength != longLength || pieces.Length <= index + 1 || !Regex.IsMatch(pieces[index++], "^91[!-~]{4}$") || !Regex.IsMatch(pieces[index++], "^92[!-~]{44}$")) return false;
        }
        var baseCode = string.Join(Gs, pieces.Take(index));
        var individual = new Dictionary<string, string>(StringComparer.Ordinal);
        while (index < pieces.Length)
        {
            var piece = pieces[index++];
            var ai = piece.StartsWith("30", StringComparison.Ordinal) || piece.StartsWith("97", StringComparison.Ordinal) ? piece[..2] : piece.Length >= 4 ? piece[..4] : "";
            if (ai.Length == 0 || !individual.TryAdd(ai, piece[ai.Length..]) || !ValidAi(ai, piece[ai.Length..])) return false;
        }
        parsed = new(baseCode, code.Substring(2, 14), pieces[0], individual); return true;
    }

    private static bool ValidAi(string ai, string value)
    {
        if (ai == "97")
        {
            try { var text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value)); return text.Length is >= 1 and <= 30 && !text.Any(char.IsControl); }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return false; }
        }
        return ai == "30" ? Regex.IsMatch(value, "^[0-9]{1,8}$") && value.Any(c => c != '0')
            : Regex.IsMatch(ai, "^(310[0-5]|311[0-5]|335[0-5])$") && Regex.IsMatch(value, "^[0-9]{6}$") && value.Any(c => c != '0');
    }
    private static string DecodeSeparators(string code) => Regex.Replace(code, @"\\u001[dDeE]", m => char.ToLowerInvariant(m.Value[^1]) == 'd' ? Gs.ToString() : Rs.ToString());
}
