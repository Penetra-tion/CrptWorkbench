namespace CrptWorkbench.Domain;

public enum ShipmentCheckState { NotChecked, Shipped, NotShipped, Unknown }

public sealed record ShipmentCheckResult(string Code, ShipmentCheckState State,
    DateTimeOffset? CheckedAt = null, string? Reason = null);
