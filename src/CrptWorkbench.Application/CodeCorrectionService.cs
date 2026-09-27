using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

/// <summary>Scoped to the UI circuit: only this service can issue and consume a preflight receipt.</summary>
public sealed class CodeCorrectionService(
    PackageDocumentService documents, ITrueApiClient client, PackageDocumentHistory history, TimeProvider clock)
{
    public const string OperationId = "true-api.code-correction";
    private readonly object _sync = new();
    private CheckSnapshot? _snapshot;
    private int _generation;
    private int _busy;
    private sealed record CheckSnapshot(Guid Id, Guid ProfileId, string ProfileKey, string Endpoint, string ValuesHash, HashSet<string> Passed);

    public static CodeCorrectionPreview ParseCodes(string text)
    {
        var codes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        foreach (var code in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (seen.Add(code)) codes.Add(code); else duplicates++;
        }
        return new(codes, duplicates);
    }

    public IReadOnlyList<string> Validate(CodeCorrectionRequest request) => CodeCorrectionValidation.Validate(request, Today);
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public void InvalidateCheck()
    {
        lock (_sync) { _snapshot = null; _generation++; }
    }

    public bool CanSubmit(Guid profileId, CodeCorrectionRequest request, Guid? checkId)
    {
        lock (_sync) return Matches(profileId, request, checkId);
    }

    private bool Matches(Guid profileId, CodeCorrectionRequest request, Guid? checkId) =>
        _snapshot is { } receipt && receipt.Id == checkId && receipt.ProfileId == profileId &&
        receipt.Endpoint == client.BaseAddress.AbsoluteUri && receipt.ValuesHash == ValuesHash(request) &&
        request.Codes.Count > 0 && request.Codes.All(receipt.Passed.Contains) && Validate(request).Count == 0;

    public async Task<CorrectionCheckResult> CheckAsync(Guid profileId, CodeCorrectionRequest request,
        IProgress<CorrectionCheckProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("Дождитесь завершения текущей операции.");
        InvalidateCheck();
        int generation;
        lock (_sync) generation = _generation;
        try
        {
            // Take an owned copy before any awaits: callers may hold mutable form lists.
            request = Copy(request);
            var errors = CodeCorrectionValidation.Validate(request, Today, includeCodes: false);
            if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
            var profile = await documents.GetProfileAsync(profileId, cancellationToken);
            var validCodes = request.Codes.Where(x => CodeCorrectionValidation.CodeError(x) is null).ToArray();
            var found = new Dictionary<string, Lookup>(StringComparer.Ordinal);
            progress?.Report(new(0, request.Codes.Count));
            foreach (var batch in validCodes.Chunk(1000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var pair in await LookupAsync(profile, batch, cancellationToken)) found[pair.Key] = pair.Value;
                progress?.Report(new(found.Count, request.Codes.Count));
            }
            var parentCodes = request.Kind is "tnved" or "vsd" or "partNumber"
                ? found.Values.Select(x => x.Info?.Parent).Where(x => !string.IsNullOrEmpty(x)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray()
                : [];
            var parents = new Dictionary<string, Lookup>(StringComparer.Ordinal);
            foreach (var batch in parentCodes.Chunk(1000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var pair in await LookupAsync(profile, batch, cancellationToken)) parents[pair.Key] = pair.Value;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var rows = request.Codes.Select(code => CheckRow(request, code, found.GetValueOrDefault(code), parents)).ToArray();
            var id = Guid.NewGuid();
            lock (_sync)
            {
                if (_generation != generation) throw new OperationCanceledException("Данные изменились во время проверки.");
                _snapshot = new(id, profileId, ProfileKey(profile), client.BaseAddress.AbsoluteUri, ValuesHash(request),
                    rows.Where(x => x.Passed).Select(x => x.Code).ToHashSet(StringComparer.Ordinal));
            }
            progress?.Report(new(rows.Length, rows.Length));
            return new(id, rows, ServerChecks(request.Kind));
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public async Task<PackageDocument> SubmitAsync(Guid profileId, CodeCorrectionRequest request, bool checkBeforeSending,
        Guid? checkId, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("Дождитесь завершения текущей операции.");
        try
        {
            request = Copy(request);
            var errors = Validate(request);
            if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
            var profile = await documents.GetProfileAsync(profileId, cancellationToken);
            lock (_sync)
            {
                if (checkBeforeSending && (!Matches(profileId, request, checkId) || _snapshot!.ProfileKey != ProfileKey(profile)))
                    throw new ArgumentException("Список или параметры изменились либо проверка не пройдена. Проверьте коды заново.");
                _snapshot = null;
                _generation++;
            }
            return await documents.SubmitCorrectionAsync(profile, request.ProductGroup, request.Kind,
                CodeCorrectionValidation.BuildJson(profile.Inn!, request), request.Codes.Count, cancellationToken);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public Task<IReadOnlyList<PackageDocument>> ListAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        documents.ListAsync(profileId, PackageOperationKind.CodeCorrection, cancellationToken);
    public Task ResumeAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        documents.ResumeAsync(profileId, id, PackageOperationKind.CodeCorrection, cancellationToken);
    public Task CheckNowAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        documents.CheckNowAsync(profileId, id, PackageOperationKind.CodeCorrection, cancellationToken);

    public static IReadOnlyList<string> ServerChecks(string kind)
    {
        var notices = new List<string> { "Право участника изменять сведения и актуальность данных на момент обработки проверяет Честный ЗНАК. Предварительная проверка не гарантирует принятие документа." };
        if (kind is "vsd") notices.Add("Честный ЗНАК проверяет ветеринарный контроль товара, ВСД, доступность ВетИС и допустимый вес/объём по ВСД.");
        if (kind is "weight" or "variableExpiration") notices.Add("Признак переменного количества или условия хранения должны соответствовать карточке товара в Национальном каталоге; их проверяет Честный ЗНАК.");
        if (kind is "permitDocs") notices.Add("Формат, действительность разрешительных документов и их соответствие карточке товара проверяет Честный ЗНАК.");
        if (kind is "partNumber") notices.Add("Наличие партии в реестре и соответствие её владельца участнику проверяет Честный ЗНАК.");
        if (kind is "licences") notices.Add("Для подземных вод номер лицензии должен содержать 10–14 цифр и букв кириллицы без пробелов. Применимость этого условия по карточке товара проверяет Честный ЗНАК.");
        return notices;
    }

    private sealed record Lookup(CisInfo? Info, string? Error);

    private async Task<Dictionary<string, Lookup>> LookupAsync(CrptProfile profile, string[] codes, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var request = new IntegrationRequest(TrueApiCodeInfoHandler.OperationId, "POST", client.BaseAddress.AbsoluteUri.TrimEnd('/') + "/cises/info",
            new Dictionary<string, string> { ["Accept"] = "application/json", ["Content-Type"] = "application/json", ["X-Correlation-Id"] = correlationId },
            JsonSerializer.Serialize(codes), correlationId, new(profile.Id, profile.Inn, profile.CertificateThumbprint));
        IntegrationExchange? exchange = null;
        string? error = null;
        var timer = Stopwatch.StartNew();
        var result = codes.ToDictionary(x => x, _ => new Lookup(null, "Не удалось проверить: ответ по коду отсутствует."), StringComparer.Ordinal);
        try
        {
            exchange = await client.ExecuteAsync(request, cancellationToken);
            var parsed = CodeInfoResults.Parse(exchange.ResponseBody);
            if (parsed is null || exchange.StatusCode is not (>= 200 and < 300) and not 404)
            {
                error = $"Не удалось проверить: HTTP {exchange.StatusCode}.";
                return codes.ToDictionary(x => x, _ => new Lookup(null, error), StringComparer.Ordinal);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in parsed.Items)
            {
                var code = item.CisInfo?.RequestedCis ?? item.CisInfo?.Cis;
                if (code is null || !result.ContainsKey(code)) continue;
                if (!seen.Add(code)) { result[code] = new(null, "Не удалось проверить: получено несколько ответов по коду."); continue; }
                result[code] = CodeInfoResults.IsFound(item)
                    ? new(item.CisInfo, null)
                    : new(item.CisInfo, item.ErrorCode == "404" ? "Код не найден." : "Не удалось получить сведения по коду.");
            }
            if (result.Values.Any(x => x.Error is not null)) error = "Не все коды удалось проверить.";
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { error = "Проверка отменена."; throw; }
        catch (Exception exception)
        {
            error = $"Не удалось проверить коды ({exception.GetType().Name}). Повторите проверку.";
            return codes.ToDictionary(x => x, _ => new Lookup(null, error), StringComparer.Ordinal);
        }
        finally
        {
            await history.RecordAsync(new PackageDocument { ProfileId = profile.Id }, request, exchange, error,
                timer.ElapsedMilliseconds, CancellationToken.None);
        }
    }

    private CorrectionCodeRow CheckRow(CodeCorrectionRequest request, string code, Lookup? lookup, IReadOnlyDictionary<string, Lookup> parents)
    {
        var errors = new List<string>();
        if (CodeCorrectionValidation.CodeError(code) is { } formatError) return new(code, null, null, [formatError]);
        if (lookup?.Error is { } error) return new(code, lookup.Info?.Status, lookup.Info?.ProductGroup, [error]);
        if (lookup?.Info is not { } info) return new(code, null, null, ["Не удалось получить сведения по коду."]);
        var group = string.IsNullOrEmpty(info.ProductGroup)
            ? CodeCorrectionRules.Groups.FirstOrDefault(x => x.Id == info.ProductGroupId)
            : CodeCorrectionRules.Groups.FirstOrDefault(x => x.Code == info.ProductGroup);
        if (group is null) errors.Add("Не удалось определить товарную группу.");
        else if (group.Code != request.ProductGroup) errors.Add($"Другая товарная группа: {group.Name}.");
        if (info.ProductGroupId is { } pg && group is not null && pg != group.Id) errors.Add("Ответ содержит противоречивую товарную группу.");
        if (string.IsNullOrEmpty(info.Status)) errors.Add("В ответе отсутствует статус кода.");
        else if (!CodeCorrectionRules.AllowedStatuses(request.Kind).Contains(info.Status))
            errors.Add($"Статус «{CodeInfoResults.StatusLabel(info.Status)}» не подходит для выбранной корректировки.");
        var packaging = CodeInfoResults.PackageTypeCode(info);
        if (string.IsNullOrEmpty(packaging)) errors.Add("Не удалось определить тип упаковки.");
        else if (!CodeCorrectionRules.AllowedPackageTypes(request.Kind).Contains(packaging)) errors.Add("Тип упаковки не допускает выбранную корректировку.");
        if (!CodeCorrectionRules.AllowsSpecialState(request.ProductGroup, request.Kind, info.StatusEx)) errors.Add($"Особое состояние «{info.StatusEx}» не допускает корректировку.");
        if (request.Kind is "tnved" or "emissionType")
        {
            if (string.IsNullOrEmpty(info.EmissionType)) errors.Add("Не удалось определить тип эмиссии.");
            else if (!CodeCorrectionRules.AllowsEmissionType(request.Kind, info.EmissionType)) errors.Add("Тип эмиссии не допускает выбранную корректировку.");
        }
        if (request.Kind == "emissionType" && !string.IsNullOrEmpty(info.Parent)) errors.Add("Код вложен в агрегат. Изменение типа эмиссии недоступно.");
        if (request.Kind is "tnved" or "vsd" or "partNumber" && !string.IsNullOrEmpty(info.Parent))
        {
            if (!parents.TryGetValue(info.Parent, out var parent) || parent.Error is not null || parent.Info is null ||
                CodeInfoResults.PackageTypeCode(parent.Info) is not ("UNIT" or "BUNDLE" or "GROUP" or "SET" or "BOX" or "ATK" or "LEVEL1" or "LEVEL2" or "LEVEL3" or "LEVEL4" or "LEVEL5"))
                errors.Add("Не удалось проверить тип родительской упаковки.");
            else if (CodeCorrectionRules.ForbiddenParentPackageTypes(request.Kind).Contains(CodeInfoResults.PackageTypeCode(parent.Info)!))
                errors.Add("Вложенность кода в эту упаковку не допускает корректировку.");
        }
        if (request.Kind == "productionDate" && CodeCorrectionValidation.TryDate(request.Values.Value, out var productionDate) && productionDate < Today.AddYears(-20) &&
            !CodeCorrectionRules.AllowsEarlyProductionDate(request.ProductGroup, info.EmissionType)) errors.Add("Для этого кода дата производства не может быть старше 20 лет.");
        if (request.Kind == "partNumber")
        {
            if (string.IsNullOrEmpty(info.Gtin)) errors.Add("Не удалось проверить GTIN партии.");
            else if (!request.Values.Value.StartsWith(info.Gtin, StringComparison.Ordinal)) errors.Add("GTIN в номере партии не совпадает с GTIN товара.");
        }
        if (request.Kind == "vsd" && Text(info, "vsd") is { } vsd && vsd == request.Values.Value) errors.Add("Новый ВСД совпадает с текущим.");
        if (request.Kind == "vsd" && Boolean(info, "veterinaryControl") == false) errors.Add("Товар не подлежит ветеринарному контролю.");
        if (request.Kind == "weight" && Boolean(info, "isVarQuantity") == false) errors.Add("Товар не имеет переменного количества.");
        if (request.Kind == "licences" && Boolean(info, "groundwaterUtilization") == false &&
            request.Values.Licences!.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x.Number, "\\A[0-9А-Яа-я]{10,14}\\z")))
            errors.Add("Для подземных вод номер лицензии: 10–14 цифр и букв кириллицы без пробелов.");
        return new(code, info.Status, group?.Code ?? info.ProductGroup, errors);
    }

    private static string? Text(CisInfo info, string key) => info.AdditionalFields?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? Boolean(CisInfo info, string key) => info.AdditionalFields?.TryGetValue(key, out var value) == true && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static string ValuesHash(CodeCorrectionRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.ProductGroup, request.Kind, request.Values }))));
    private static string ProfileKey(CrptProfile profile) => $"{profile.Inn}|{profile.CertificateThumbprint}";
    private static CodeCorrectionRequest Copy(CodeCorrectionRequest request) => request with
    {
        Codes = request.Codes.ToArray(),
        Values = request.Values with { PermitDocuments = request.Values.PermitDocuments?.ToArray(), Licences = request.Values.Licences?.ToArray(), Expirations = request.Values.Expirations?.ToArray() }
    };
}
