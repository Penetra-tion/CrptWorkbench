using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record TransformationRow(int Line, string Item, string Package, bool NestedPackage = false);
public sealed record TransformationPreview(IReadOnlyList<TransformationRow> Rows, IReadOnlyList<string> Errors)
{
    public int PackageCount => Rows.Select(x => x.Package).Distinct(StringComparer.Ordinal).Count();
}

public sealed class PackageTransformationService(PackageDocumentService documents)
{
    public const string OperationId = "true-api.package-transformation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    internal static readonly HashSet<string> AllowedGroups = new(StringComparer.Ordinal)
    {
        "carparts", "antiseptic", "grocery", "nabeer", "bio", "bicycle", "vetpharma", "perfumery",
        "toys", "cableraw", "conserve", "petfood", "wheelchairs", "milk", "seafood", "autofluids", "shoes",
        "opticfiber", "heater", "chemistry", "books", "beer", "fire", "polymer", "lp", "radio",
        "vegetableoil", "alcohol", "sweets", "softdrinks", "construction", "water", "electronics", "tires"
    };
    private static readonly Regex CodeCharacters = new("^[A-Za-z0-9%&'\"()*+,_./:;<=>?!-]{18,74}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static TransformationPreview Parse(string text) => Parse(text, enforceLimits: true);

    internal static TransformationPreview Parse(string text, bool enforceLimits)
    {
        var rows = new List<TransformationRow>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (enforceLimits && Encoding.UTF8.GetByteCount(text) > 1_000_000) return new([], ["Файл слишком большой: максимум 1 МБ."]);
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length != 2)
            {
                errors.Add($"Строка {i + 1}: нужны два кода, разделённые пробелом или табуляцией.");
                continue;
            }
            var item = MarkingCodeInput.TrimAfterGroupSeparator(cells[0].Trim());
            var package = MarkingCodeInput.TrimAfterGroupSeparator(cells[1].Trim());
            if (!CodeCharacters.IsMatch(item) || !CodeCharacters.IsMatch(package))
            {
                errors.Add($"Строка {i + 1}: код должен содержать 18–74 допустимых символа без управляющих знаков.");
                continue;
            }
            if (item == package)
            {
                errors.Add($"Строка {i + 1}: вложение совпадает с упаковкой.");
                continue;
            }
            if (!seen.Add(item))
            {
                errors.Add($"Строка {i + 1}: код вложения уже встречался в файле.");
                continue;
            }
            rows.Add(new(i + 1, item, package));
        }
        if (rows.Count == 0) errors.Add("Добавьте хотя бы одну строку.");
        if (enforceLimits && rows.Count > 1000) errors.Add("За один раз допускается не более 1000 строк.");
        return new(rows, errors);
    }

    public async Task<IReadOnlyList<PackageDocument>> SubmitAsync(Guid profileId, string group, string action,
        IReadOnlyList<TransformationRow> rows, CancellationToken cancellationToken = default)
    {
        if (action is not ("ADDING" or "REMOVING")) throw new ArgumentException("Выберите добавление или изъятие.");
        if (!AllowedGroups.Contains(group)) throw new ArgumentException("Товарная группа не поддерживает трансформацию упаковок.");
        var checkedRows = Parse(string.Join('\n', rows.Select(x => x.Item + "\t" + x.Package)));
        if (checkedRows.Errors.Count > 0 || checkedRows.Rows.Count != rows.Count)
            throw new ArgumentException(string.Join(" ", checkedRows.Errors));
        var profile = await documents.GetProfileAsync(profileId, cancellationToken);
        var drafts = rows.GroupBy(x => x.Package, StringComparer.Ordinal).Select(package => new PackageDocumentDraft(
            package.Key, package.Count(), JsonSerializer.Serialize(new
            {
                participant_inn = profile.Inn,
                reaggregation_type = action,
                uitu = package.Key,
                uit_uitu_list = package.Select(x => new { uit_uitu = x.NestedPackage ? null : x.Item, kitu = x.NestedPackage ? x.Item : null }).ToArray()
            }, JsonOptions))).ToArray();
        return await documents.SubmitAsync(profile, group, PackageOperationKind.Transformation, action, null, drafts, cancellationToken);
    }

    public Task<RecordPage<PackageDocument>> ListPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) =>
        documents.ListPageAsync(profileId, PackageOperationKind.Transformation, cursor, cancellationToken);

    public Task<IReadOnlyList<PackageDocument>> GetManyAsync(Guid profileId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        documents.GetManyAsync(profileId, PackageOperationKind.Transformation, ids, cancellationToken);

    public Task<IReadOnlyList<PackageDocument>> ListAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        documents.ListAsync(profileId, PackageOperationKind.Transformation, cancellationToken);
    public Task ResumeAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        documents.ResumeAsync(profileId, id, PackageOperationKind.Transformation, cancellationToken);
    public Task CheckNowAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        documents.CheckNowAsync(profileId, id, PackageOperationKind.Transformation, cancellationToken);
}
