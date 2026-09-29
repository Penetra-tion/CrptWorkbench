using CrptWorkbench.Application;
using Microsoft.AspNetCore.WebUtilities;

namespace CrptWorkbench.Web;

public sealed record ConsoleDestination(ApiOperationDescriptor? Operation, string? Code = null, bool UnknownOperation = false);

public static class OperationNavigation
{
    public static string ConsoleUrl(string operationId) => $"/console?operation={Uri.EscapeDataString(operationId)}";

    public static string DocumentUrl(CodeHistoryDocumentSelection selection)
    {
        var values = new Dictionary<string, string?>
        {
            ["document"] = selection.DocumentId,
            ["source"] = selection.Source == CrptWorkbench.Domain.ApiSystem.Suz ? "suz" : "true-api"
        };
        if (selection.ProductGroup == "furs") values["productGroup"] = "furs";
        return QueryHelpers.AddQueryString("/console", values);
    }

    public static bool IsOperationPath(string path) => path.TrimEnd('/').ToLowerInvariant() is
        "/operations" or "/console" or "/special-features" or "/package-aggregation" or
        "/package-transformation" or "/code-correction";

    public static ConsoleDestination ResolveConsole(string queryString, IReadOnlyList<ApiOperationDescriptor> operations)
    {
        var query = QueryHelpers.ParseQuery(queryString);
        if (query.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code.ToString()))
            return new(operations.FirstOrDefault(x => x.Id == TrueApiCodeInfoHandler.OperationId), code.ToString());

        if (!query.TryGetValue("operation", out var id)) return new(null);
        var operation = operations.FirstOrDefault(x => string.Equals(x.Id, id.ToString(), StringComparison.OrdinalIgnoreCase));
        return new(operation, UnknownOperation: operation is null);
    }
}
