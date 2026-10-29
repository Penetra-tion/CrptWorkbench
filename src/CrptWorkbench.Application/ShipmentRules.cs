using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

// True API v733 §6.3: identities use document numbers AND dates AND both participants.
public sealed record ShipmentProduct(string Code, string? CodeType = "EXEMPLARY", string? ElementType = null);

public sealed record ShipmentDocument(string Id, string Type, string? Status, DateTimeOffset? EventAt,
    string? SellerInn, string? BuyerInn, IReadOnlyList<ShipmentProduct> Products,
    string? CancelledDocumentId = null, string? UpdNumber = null, DateTimeOffset? UpdDate = null,
    string? UcdNumber = null, DateTimeOffset? UcdDate = null, string? FixNumber = null,
    DateTimeOffset? FixDate = null, bool Complete = true, bool HasErrors = false,
    string? ReferencedUpdFixNumber = null, DateTimeOffset? ReferencedUpdFixDate = null);

public static class ShipmentRules
{
    public static bool IsShipmentType(string? type) => type is
        "UNIVERSAL_TRANSFER_DOCUMENT" or "UNIVERSAL_TRANSFER_DOCUMENT_FIX" or
        "UNIVERSAL_CORRECTION_DOCUMENT" or "UNIVERSAL_CORRECTION_DOCUMENT_FIX" or
        "FIXATION" or "FIXATION_CANCEL" or "UNIVERSAL_CANCEL_DOCUMENT";

    public static bool IsInactive(ShipmentDocument doc) => doc.HasErrors || doc.Status is
        "CANCELED" or "CANCELLED" or "CHECKED_NOT_OK" or "PARSE_ERROR" or "PROCESSING_ERROR" or "FAILED" or "ERROR" or "REJECTED";

    public static bool IsFinished(ShipmentDocument doc) => doc.Status == "CHECKED_OK";
    private static bool IsCancel(ShipmentDocument doc) => doc.Type is "FIXATION_CANCEL" or "UNIVERSAL_CANCEL_DOCUMENT";
    private static bool IsFix(ShipmentDocument doc) => doc.Type.EndsWith("_FIX", StringComparison.Ordinal);
    private static bool IsCorrection(ShipmentDocument doc) => doc.Type.StartsWith("UNIVERSAL_CORRECTION_DOCUMENT", StringComparison.Ordinal);
    private static bool SameUpd(ShipmentDocument a, ShipmentDocument b) =>
        !string.IsNullOrEmpty(a.UpdNumber) && a.UpdDate is not null &&
        a.UpdNumber == b.UpdNumber && a.UpdDate == b.UpdDate && a.SellerInn == b.SellerInn && a.BuyerInn == b.BuyerInn;
    private static bool SameUcd(ShipmentDocument a, ShipmentDocument b) =>
        !string.IsNullOrEmpty(a.UcdNumber) && a.UcdDate is not null &&
        a.UcdNumber == b.UcdNumber && a.UcdDate == b.UcdDate && SameUpd(a, b) &&
        a.ReferencedUpdFixNumber == b.ReferencedUpdFixNumber && a.ReferencedUpdFixDate == b.ReferencedUpdFixDate;

    public static ShipmentCheckResult Evaluate(string code, string? inn, IEnumerable<ShipmentDocument> documents)
    {
        ShipmentCheckResult Unknown(string reason) => new(code, ShipmentCheckState.Unknown, DateTimeOffset.UtcNow, reason);
        if (string.IsNullOrWhiteSpace(inn)) return Unknown("В активном профиле не указан ИНН.");
        var groups = documents.Where(d => IsShipmentType(d.Type)).GroupBy(d => d.Id, StringComparer.Ordinal).ToArray();
        if (groups.Any(g => g.Skip(1).Any(d => !Equivalent(g.First(), d))))
            return Unknown("История содержит противоречивые сведения одного документа.");
        var all = groups.Select(g => g.First()).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var candidates = all.Values.Where(d => !IsInactive(d)).ToArray();
        foreach (var doc in candidates)
        {
            if (!IsCancel(doc) && doc.SellerInn is not null && doc.BuyerInn is not null && doc.SellerInn != inn && doc.BuyerInn != inn)
                continue;
            if (!IsFinished(doc)) return Unknown("Есть документ с незавершённой обработкой.");
            if (!doc.Complete) return Unknown("Сведения документа получены не полностью.");
            if (IsCancel(doc) && (doc.CancelledDocumentId is null || !all.ContainsKey(doc.CancelledDocumentId)))
                return Unknown("Не удалось связать отмену с исходным документом.");
            if (IsCancel(doc) && all.TryGetValue(doc.CancelledDocumentId!, out var target) &&
                (doc.EventAt is null || target.EventAt is null || doc.EventAt <= target.EventAt))
                return Unknown("Время отмены не следует за временем исходного документа.");
            if (!IsCancel(doc) && doc.Products.Any(p => p.CodeType is not (null or "EXEMPLARY" or "OSU")))
                return Unknown("Документ содержит неизвестный вид учёта кодов.");
        }
        var cancelledBy = candidates.Where(IsCancel).GroupBy(d => d.CancelledDocumentId!)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var activeCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var cycle = false;
        bool Active(ShipmentDocument doc)
        {
            if (IsInactive(doc)) return false;
            if (activeCache.TryGetValue(doc.Id, out var active)) return active;
            if (!visiting.Add(doc.Id)) { cycle = true; return false; }
            active = !cancelledBy.TryGetValue(doc.Id, out var cancellations) || !cancellations.Any(Active);
            visiting.Remove(doc.Id);
            activeCache[doc.Id] = active;
            return active;
        }
        var effective = candidates.Where(Active).Where(d => !IsCancel(d)).ToArray();
        if (cycle) return Unknown("Цепочка отмен содержит циклическую связь.");
        effective = effective.Where(d => d.SellerInn == inn || d.BuyerInn == inn || d.SellerInn is null || d.BuyerInn is null).ToArray();
        if (effective.Any(d => d.SellerInn is null || d.BuyerInn is null))
            return Unknown("Не определены участники движения кода.");
        var roots = all.Values.Where(d => d.Type is "UNIVERSAL_TRANSFER_DOCUMENT" or "FIXATION").ToArray();
        var changes = effective.Where(d => IsFix(d) || IsCorrection(d)).ToArray();
        foreach (var change in changes)
        {
            if (roots.Count(root => SameUpd(root, change)) != 1)
                return Unknown("Не удалось однозначно связать исправление или корректировку с УПД.");
            if (IsFix(change) && (change.FixDate is null || string.IsNullOrWhiteSpace(change.FixNumber)))
                return Unknown("Недостаточно сведений о версии исправления.");
        }
        var movements = new List<(DateTimeOffset At, bool Outgoing)>();
        foreach (var root in roots.Where(Active))
        {
            if (root.SellerInn != inn && root.BuyerInn != inn) continue;
            var related = changes.Where(d => SameUpd(root, d)).ToArray();
            var fixes = related.Where(d => d.Type == "UNIVERSAL_TRANSFER_DOCUMENT_FIX").ToArray();
            if (fixes.GroupBy(d => d.FixDate).Any(g => g.Count() > 1))
                return Unknown("Порядок исправлений УПД неоднозначен.");
            var version = fixes.OrderBy(d => d.FixDate).LastOrDefault() ?? root;
            if (related.Any(d => d.EventAt is null || root.EventAt is null || d.EventAt <= root.EventAt))
                return Unknown("Время исправления или корректировки не следует за исходным УПД.");
            foreach (var correction in related.Where(IsCorrection))
            {
                if (correction.ReferencedUpdFixNumber is null && correction.ReferencedUpdFixDate is null)
                {
                    if (version != root) return Unknown("Корректировка не определяет действующую версию исправленного УПД.");
                    continue;
                }
                var references = fixes.Where(f => f.FixNumber == correction.ReferencedUpdFixNumber &&
                    f.FixDate == correction.ReferencedUpdFixDate).ToArray();
                if (correction.ReferencedUpdFixNumber is null || correction.ReferencedUpdFixDate is null ||
                    references.Length != 1 || references[0].Id != version.Id)
                    return Unknown("Корректировка ссылается на отсутствующее или неоднозначное исправление УПД.");
                if (correction.EventAt <= references[0].EventAt)
                    return Unknown("Время корректировки не следует за исправлением УПД.");
            }
            if (!version.Complete) return Unknown("Состав документа получен не полностью.");
            if (version.Products.Any(p => p.CodeType == "OSU") && !version.Products.Any(p => p.Code == code && p.CodeType != "OSU"))
                return Unknown("Объёмно-сортовой учёт не подтверждает движение конкретного кода.");
            var included = version.Products.Any(p => p.Code == code && p.CodeType != "OSU");
            var at = root.EventAt;
            var priorIncludes = root.Products.Any(p => p.Code == code && p.CodeType != "OSU");
            foreach (var fix in fixes.OrderBy(d => d.FixDate))
            {
                var nextIncludes = fix.Products.Any(p => p.Code == code && p.CodeType != "OSU");
                if (!priorIncludes && nextIncludes) at = fix.EventAt;
                priorIncludes = nextIncludes;
            }
            var correctionRoots = related.Where(d => d.Type == "UNIVERSAL_CORRECTION_DOCUMENT").ToArray();
            if (related.Any(d => d.Type == "UNIVERSAL_CORRECTION_DOCUMENT_FIX" && correctionRoots.Count(r => SameUcd(r, d)) != 1))
                return Unknown("Не удалось связать исправление УКД с исходной корректировкой.");
            if (correctionRoots.Any(c => related.Where(d => d.Type == "UNIVERSAL_CORRECTION_DOCUMENT_FIX" && SameUcd(c, d))
                .GroupBy(d => d.FixDate).Any(g => g.Count() > 1)))
                return Unknown("Порядок исправлений УКД неоднозначен.");
            var correctionVersions = correctionRoots.Select(c => related.Where(d => d.Type == "UNIVERSAL_CORRECTION_DOCUMENT_FIX" && SameUcd(c, d))
                .Append(c).OrderBy(d => d.FixDate ?? DateTimeOffset.MinValue).Last()).ToArray();
            if (correctionVersions.Any(d => d.EventAt is null) || correctionVersions.GroupBy(d => d.EventAt).Any(g => g.Count() > 1))
                return Unknown("Порядок корректировок неоднозначен.");
            foreach (var correction in correctionVersions.OrderBy(d => d.EventAt))
            {
                if (correction.Products.Any(p => p.CodeType == "OSU") && !correction.Products.Any(p => p.Code == code && p.CodeType != "OSU"))
                    return Unknown("Корректировка в объёмно-сортовом учёте не подтверждает движение кода.");
                var matching = correction.Products.Where(p => p.Code == code && p.CodeType != "OSU").ToArray();
                if (matching.Any(p => p.ElementType is not ("BEFORE" or "AFTER")))
                    return Unknown("Не определён состав кода до и после корректировки.");
                var before = matching.Any(p => p.ElementType == "BEFORE");
                var after = matching.Any(p => p.ElementType == "AFTER");
                if (!before && !after) continue;
                if (before != included) return Unknown("Состав корректировки противоречит действующей версии УПД.");
                if (!included && after) at = correction.EventAt;
                included = after;
            }
            if (!included || root.SellerInn == root.BuyerInn) continue;
            if (at is null) return Unknown("Не определено время движения кода.");
            movements.Add((at.Value, root.SellerInn == inn));
        }
        if (movements.GroupBy(m => m.At).Any(g => g.Select(m => m.Outgoing).Distinct().Count() > 1))
            return Unknown("Одновременные входящие и исходящие движения не имеют однозначного порядка.");
        var shipped = movements.OrderBy(m => m.At).LastOrDefault().Outgoing;
        return new(code, shipped ? ShipmentCheckState.Shipped : ShipmentCheckState.NotShipped, DateTimeOffset.UtcNow);
    }

    private static bool Equivalent(ShipmentDocument a, ShipmentDocument b) =>
        a with { Products = b.Products } == b && a.Products.SequenceEqual(b.Products);
}
