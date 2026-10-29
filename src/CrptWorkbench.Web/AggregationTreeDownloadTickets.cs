using System.Text.Json;
using CrptWorkbench.Domain;
using Microsoft.AspNetCore.DataProtection;

namespace CrptWorkbench.Web;

public sealed class AggregationTreeDownloadTickets(IDataProtectionProvider protection)
{
    public sealed record Ticket(Guid ProfileId, Guid JobId, CrptIntegrationMode Mode, Guid ExportId, bool Rejected);
    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("aggregation-tree-download-v1").ToTimeLimitedDataProtector();
    public string Issue(Guid profileId, Guid jobId, CrptIntegrationMode mode, Guid exportId) => Protect(new(profileId, jobId, mode, exportId, false));
    public string IssueRejected(Guid profileId, Guid jobId, CrptIntegrationMode mode) => Protect(new(profileId, jobId, mode, Guid.Empty, true));
    private string Protect(Ticket ticket) => _protector.Protect(JsonSerializer.Serialize(ticket), TimeSpan.FromMinutes(5));
    public bool TryRead(string token, bool rejected, out Ticket? ticket)
    {
        ticket = null;
        try
        {
            var value = JsonSerializer.Deserialize<Ticket>(_protector.Unprotect(token));
            if (value is null || value.ProfileId == Guid.Empty || value.JobId == Guid.Empty || !Enum.IsDefined(value.Mode) ||
                value.Rejected != rejected || (!rejected && value.ExportId == Guid.Empty)) return false;
            ticket = value; return true;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException) { return false; }
    }
}
