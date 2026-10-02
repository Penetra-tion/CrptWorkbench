using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public static class InputValidation
{
    public static InputValidationResult Profile(CrptProfile profile)
    {
        var issues = new List<InputIssue>();
        var name = profile.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200 || (profile.Name?.Any(char.IsControl) ?? false))
            issues.Add(new(nameof(profile.Name), "Название должно содержать от 1 до 200 символов без управляющих знаков."));

        var inn = profile.Inn?.Trim();
        if (!string.IsNullOrEmpty(inn) && (inn.Length is not (10 or 12) || inn.Any(c => !char.IsAsciiDigit(c))))
            issues.Add(new(nameof(profile.Inn), "ИНН должен содержать 10 или 12 цифр."));

        CheckUrl(profile.NationalCatalogProductionBaseUrl, nameof(profile.NationalCatalogProductionBaseUrl), "Mock Base URL Национального каталога", issues);
        CheckUrl(profile.SuzProductionBaseUrl, nameof(profile.SuzProductionBaseUrl), "Base URL СУЗ", issues);
        CheckUrl(profile.TrueApiProductionBaseUrl, nameof(profile.TrueApiProductionBaseUrl), "Base URL True API", issues);

        var omsId = profile.SuzOmsId?.Trim();
        if (!string.IsNullOrEmpty(omsId) && (!Guid.TryParseExact(omsId, "D", out _) || omsId.Length != 36))
            issues.Add(new(nameof(profile.SuzOmsId), "OmsId должен быть UUID в формате 00000000-0000-0000-0000-000000000000."));

        var connectionId = profile.SuzConnectionId?.Trim();
        if (!string.IsNullOrEmpty(connectionId) && (connectionId.Length > 128 || connectionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            issues.Add(new(nameof(profile.SuzConnectionId), "ConnectionId: не более 128 латинских букв, цифр, дефисов или подчёркиваний."));

        var thumbprint = profile.CertificateThumbprint?.Trim();
        if (!string.IsNullOrEmpty(thumbprint) && (thumbprint.Length > 128 || thumbprint.Any(c => !char.IsAsciiHexDigit(c))))
            issues.Add(new(nameof(profile.CertificateThumbprint), "Выберите сертификат из списка."));

        if (!Enum.IsDefined(profile.TraceXDatabaseProvider))
            issues.Add(new(nameof(profile.TraceXDatabaseProvider), "Выберите поддерживаемый тип базы TraceX."));

        return new(issues);
    }

    public static InputValidationResult Operation(string operationId, IReadOnlyDictionary<string, string> values)
    {
        var issues = new List<InputIssue>();
        try
        {
            switch (operationId)
            {
                case TrueApiCodeInfoHandler.OperationId:
                    TrueApiCodeInfoHandler.Parse(values);
                    break;
                case TrueApiBulkCodeInfoHandler.OperationId:
                    TrueApiBulkCodeInfoHandler.Parse(values);
                    break;
                case TrueApiAggregationTreeHandler.OperationId:
                    TrueApiAggregationTreeHandler.Parse(values);
                    break;
                case TrueApiDocumentStatusHandler.OperationId:
                    TrueApiDocumentStatusHandler.Parse(values);
                    break;
                case SuzDocumentStatusHandler.OperationId:
                    SuzDocumentStatusHandler.Parse(values);
                    break;
                case MercuryVsdInfoHandler.OperationId:
                    MercuryVsdInfoHandler.Parse(values);
                    break;
                case PermitDocumentLookupHandler.OperationId:
                    PermitDocumentNumbers.Parse(values.GetValueOrDefault("number"));
                    break;
                case NationalCatalogProductHandler.OperationId:
                    var gtin = values.GetValueOrDefault("gtin")?.Trim() ?? "";
                    if (gtin.Length is not (8 or 12 or 13 or 14) || gtin.Any(c => !char.IsAsciiDigit(c)))
                        issues.Add(new("gtin", "GTIN должен содержать 8, 12, 13 или 14 цифр."));
                    break;
                case "suz.order-status":
                    var rawOrderId = values.GetValueOrDefault("value") ?? "";
                    var orderId = rawOrderId.Trim();
                    if (orderId.Length is < 1 or > 256 || rawOrderId.Any(char.IsControl))
                        issues.Add(new("value", "Order ID должен содержать от 1 до 256 символов без управляющих знаков."));
                    break;
            }
        }
        catch (ArgumentException exception)
        {
            var field = operationId is TrueApiCodeInfoHandler.OperationId or TrueApiBulkCodeInfoHandler.OperationId ? "codes" :
                operationId == TrueApiAggregationTreeHandler.OperationId ? "code" :
                operationId == MercuryVsdInfoHandler.OperationId ? "uid" :
                operationId == PermitDocumentLookupHandler.OperationId ? "number" : "documentId";
            if (operationId == TrueApiDocumentStatusHandler.OperationId && exception.Message.Contains("товарной группы", StringComparison.Ordinal)) field = "productGroup";
            issues.Add(new(field, exception.Message));
        }
        return new(issues);
    }

    private static void CheckUrl(string? value, string field, string label, List<InputIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (value != value.Trim() || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            issues.Add(new(field, $"{label}: укажите абсолютный адрес HTTP/HTTPS без логина, параметров и фрагмента."));
    }
}
