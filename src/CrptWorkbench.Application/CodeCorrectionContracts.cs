namespace CrptWorkbench.Application;

public sealed record CorrectionPermitDocument(int Type, string Number, string Date);
public sealed record CorrectionLicence(string Number, string Date);
public sealed record CorrectionExpiration(int Index, string Date);
public sealed record CorrectionValues(
    string Value = "", int Operation = 0,
    IReadOnlyList<CorrectionPermitDocument>? PermitDocuments = null,
    IReadOnlyList<CorrectionLicence>? Licences = null,
    IReadOnlyList<CorrectionExpiration>? Expirations = null);
public sealed record CodeCorrectionRequest(string ProductGroup, string Kind, CorrectionValues Values, IReadOnlyList<string> Codes);
public sealed record CorrectionCodeRow(string Code, string? Status, string? ProductGroup, IReadOnlyList<string> Reasons)
{
    public bool Passed => Reasons.Count == 0;
}
public sealed record CodeCorrectionPreview(IReadOnlyList<string> Codes, int DuplicateCount);
public sealed record CorrectionCheckProgress(int Checked, int Total);
public sealed record CorrectionCheckResult(Guid CheckId, IReadOnlyList<CorrectionCodeRow> Rows, IReadOnlyList<string> ServerChecks);
