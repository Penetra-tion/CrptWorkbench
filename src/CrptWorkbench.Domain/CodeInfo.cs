using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrptWorkbench.Domain;

public sealed record CodeInfoItem(CisInfo? CisInfo = null, string? ErrorCode = null, string? ErrorMessage = null)
{
    public CodeDocumentHistory? DocumentHistory { get; init; }
}

public sealed record CodeDocumentHistory(bool? Paid, IReadOnlyList<CodeHistoryDocument> Documents, string? Warning = null);

public sealed record CodeHistoryDocument(
    string? DocumentId,
    DateTimeOffset? ProcessedAt,
    string? Type,
    string? Status,
    string? SenderName,
    string? SenderInn,
    string? ReceiverName,
    string? ReceiverInn);

public sealed record CodeInfoPermitDocument
{
    public string? Type { get; init; }
    public string? Number { get; init; }
    public string? Date { get; init; }
    public string? WellNumber { get; init; }
    public int? Indx { get; init; }
    public int? StatusGroup { get; init; }
}

public sealed record CodeInfoPermitRequirementDetails
{
    public string? NotRequiredReason { get; init; }
    public bool? IsRequired { get; init; }
}

public sealed record CodeInfoExpiration
{
    public DateTimeOffset? ExpirationStorageDate { get; init; }
    public int? StorageConditionId { get; init; }
    public string? StorageConditionName { get; init; }
}

public sealed record CisInfo
{
    public string? RequestedCis { get; init; }
    public string? Cis { get; init; }
    public string? Gtin { get; init; }
    public string? ProductName { get; init; }
    public int? ProductGroupId { get; init; }
    public string? ProductGroup { get; init; }
    public string? Brand { get; init; }
    public string? Status { get; init; }
    public string? StatusEx { get; init; }
    public string? WithdrawReason { get; init; }
    public string? WithdrawReasonOther { get; init; }
    public string? OwnerInn { get; init; }
    public string? OwnerName { get; init; }
    public string? ProducerInn { get; init; }
    public string? ProducerName { get; init; }
    public string? GeneralPackageType { get; init; }
    public string? PackageType { get; init; }
    public string? EmissionType { get; init; }
    public DateTimeOffset? EmissionDate { get; init; }
    public DateTimeOffset? ProducedDate { get; init; }
    public DateTimeOffset? ApplicationDate { get; init; }
    public DateTimeOffset? IntroducedDate { get; init; }
    public DateTimeOffset? ExpirationDate { get; init; }
    public IReadOnlyList<CodeInfoExpiration>? Expirations { get; init; }
    public IReadOnlyList<CodeInfoPermitDocument>? CertDoc { get; init; }
    public CodeInfoPermitRequirementDetails? CertDocRequirementDetails { get; init; }
    public IReadOnlyList<string>? Child { get; init; }
    public string? Parent { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; init; }
}
