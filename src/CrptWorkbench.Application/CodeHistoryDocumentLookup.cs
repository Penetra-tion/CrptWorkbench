using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record CodeHistoryDocumentSelection(string DocumentId, ApiSystem Source, string? ProductGroup);

public sealed record CodeHistoryDocumentLookupResult(ApiSystem System, ApiExecutionResult Result, bool NotFound,
    bool CheckedBothSystems);

public sealed class CodeHistoryDocumentLookup(ExecuteApiOperationService executor)
{
    public static bool IsValidId(string? id) => id is not null &&
        TryTrueApi(id);

    private static bool TryTrueApi(string id)
    {
        try
        {
            TrueApiDocumentStatusHandler.Parse(new Dictionary<string, string> { ["documentId"] = id });
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public async Task<CodeHistoryDocumentLookupResult> ExecuteAsync(CodeHistoryDocumentSelection selection,
        Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(selection.DocumentId)) throw new ArgumentException("Недопустимый идентификатор документа.");
        if (selection.Source is not (ApiSystem.TrueApi or ApiSystem.Suz))
            throw new ArgumentException("Неизвестная система документа.");

        if (selection.Source == ApiSystem.Suz)
        {
            var suzOnly = await ExecuteSuzAsync(selection.DocumentId, profileId, cancellationToken);
            return new(ApiSystem.Suz, suzOnly, IsSuzMissing(suzOnly), false);
        }

        var values = new Dictionary<string, string>
        {
            ["documentId"] = selection.DocumentId,
            ["productGroup"] = selection.ProductGroup == "furs" ? "furs" : ""
        };
        var trueApi = await executor.ExecuteAsync(TrueApiDocumentStatusHandler.OperationId, values, profileId, cancellationToken);
        if (!IsTrueApiMissing(trueApi)) return new(ApiSystem.TrueApi, trueApi, false, false);
        if (!Guid.TryParseExact(selection.DocumentId, "D", out _))
            return new(ApiSystem.TrueApi, trueApi, true, false);

        var suz = await ExecuteSuzAsync(selection.DocumentId, profileId, cancellationToken);
        return new(ApiSystem.Suz, suz, IsSuzMissing(suz), true);
    }

    public static bool IsTrueApiMissing(ApiExecutionResult result) => result.StatusCode == 404 ||
        result.Success && DocumentStatusResults.Parse(result.ResponseBody) is { Items.Count: 0 };

    public static bool IsSuzMissing(ApiExecutionResult result) => result.StatusCode == 404 ||
        result.Success && SuzDocumentStatusResults.Parse(result.ResponseBody) is { Results.Count: 0 };

    private Task<ApiExecutionResult> ExecuteSuzAsync(string id, Guid profileId, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(SuzDocumentStatusHandler.OperationId,
            new Dictionary<string, string> { ["documentId"] = id }, profileId, cancellationToken);
}
