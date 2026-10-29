using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record AggregationUnit(
    [property: JsonPropertyName("unitSerialNumber")] string UnitSerialNumber,
    [property: JsonPropertyName("aggregationType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AggregationType,
    [property: JsonPropertyName("sntins")] IReadOnlyList<string> Sntins);

public sealed record AggregationDocumentBody(
    [property: JsonPropertyName("participantId")] string ParticipantId,
    [property: JsonPropertyName("aggregationUnits")] IReadOnlyList<AggregationUnit> AggregationUnits);

public sealed class PackageAggregationService(PackageDocumentService documents)
{
    public const string OperationId = "true-api.package-aggregation";
    private static readonly Regex ForbiddenAggregate = new("^02[0-9]{14}37[0-9]{1,8}(3103[0-9]{9})?$", RegexOptions.CultureInvariant);

    public static TransformationPreview Parse(string text) => Parse(text, createSet: false);

    public static TransformationPreview Parse(string text, bool createSet)
    {
        var parsed = PackageTransformationService.Parse(text, enforceLimits: false);
        var errors = parsed.Errors.ToList();
        var parents = parsed.Rows.Select(x => x.Package).ToHashSet(StringComparer.Ordinal);
        foreach (var row in parsed.Rows)
        {
            if (!createSet && ForbiddenAggregate.IsMatch(row.Package))
                errors.Add($"Строка {row.Line}: код упаковки соответствует запрещённой маске объёмно-сортового учёта.");
            if (parents.Contains(row.Item))
                errors.Add($"Строка {row.Line}: упаковка создаётся и вкладывается в этой же загрузке. Сначала сформируйте нижний уровень и дождитесь успешной обработки.");
        }
        return new(parsed.Rows, errors);
    }

    public async Task<IReadOnlyList<PackageDocument>> SubmitAsync(Guid profileId, string group, string packageType,
        IReadOnlyList<TransformationRow> rows, CancellationToken cancellationToken = default)
    {
        if (packageType is not ("BOX" or "GROUP" or "AUTO" or "SET")) throw new ArgumentException("Неизвестный режим формирования упаковки.");
        if (!PackageTransformationService.AllowedGroups.Contains(group)) throw new ArgumentException("Товарная группа не поддерживает формирование упаковок.");
        if (packageType == "SET" && !PackageSetRules.SupportsGroup(group))
            throw new ArgumentException("Товарная группа не поддерживает формирование наборов.");
        var parsed = Parse(string.Join('\n', rows.Select(x => x.Item + "\t" + x.Package)), packageType == "SET");
        if (parsed.Errors.Count > 0 || parsed.Rows.Count != rows.Count ||
            !parsed.Rows.Select(x => (x.Item, x.Package)).SequenceEqual(rows.Select(x => (x.Item, x.Package))))
            throw new ArgumentException(parsed.Errors.Count > 0 ? string.Join(" ", parsed.Errors) : "Коды содержат лишние пробелы или разделители.");
        var profile = await documents.GetProfileAsync(profileId, cancellationToken);
        var drafts = parsed.Rows.GroupBy(x => x.Package, StringComparer.Ordinal).Select(package => new PackageDocumentDraft(
            package.Key, package.Count(), JsonSerializer.Serialize(new AggregationDocumentBody(profile.Inn!,
                [new AggregationUnit(package.Key, packageType == "SET" ? null : "AGGREGATION", package.Select(x => x.Item).ToArray())])),
            package.Select(x => x.Item).Append(package.Key).ToHashSet(StringComparer.Ordinal))).ToArray();
        return await documents.SubmitAsync(profile, group, PackageOperationKind.Aggregation, "AGGREGATION", packageType, drafts, cancellationToken);
    }

    public Task<RecordPage<PackageDocument>> ListPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) =>
        documents.ListPageAsync(profileId, PackageOperationKind.Aggregation, cursor, cancellationToken);

    public Task<IReadOnlyList<PackageDocument>> GetManyAsync(Guid profileId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        documents.GetManyAsync(profileId, PackageOperationKind.Aggregation, ids, cancellationToken);

    public Task<IReadOnlyList<PackageDocument>> ListAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        documents.ListAsync(profileId, PackageOperationKind.Aggregation, cancellationToken);
    public Task ResumeAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        documents.ResumeAsync(profileId, id, PackageOperationKind.Aggregation, cancellationToken);
    public Task CheckNowAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        documents.CheckNowAsync(profileId, id, PackageOperationKind.Aggregation, cancellationToken);
}
