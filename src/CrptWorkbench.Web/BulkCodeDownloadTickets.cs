using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Web;

public sealed class BulkCodeDownloadTickets(IDataProtectionProvider protection)
{
    public string Issue(Guid profileId, Guid jobId, IReadOnlyCollection<string> statuses, bool shippedOnly,
        string inn, CrptIntegrationMode mode) =>
        _protector.Protect(JsonSerializer.Serialize(new Ticket(profileId, jobId, statuses.ToArray(), shippedOnly, inn, mode)), TimeSpan.FromMinutes(5));

    public bool TryRead(string token, out Guid profileId, out Guid jobId, out string[] statuses,
        out bool shippedOnly, out string? inn, out CrptIntegrationMode mode)
    {
        shippedOnly = false; inn = null; mode = CrptIntegrationMode.Mock;
        profileId = jobId = Guid.Empty;
        statuses = [];
        try
        {
            var ticket = JsonSerializer.Deserialize<Ticket>(_protector.Unprotect(token));
            if (ticket is null || ticket.ProfileId == Guid.Empty || ticket.JobId == Guid.Empty ||
                ticket.Statuses.Any(x => x.Length is < 1 or > 40 || x.Any(c => !char.IsAsciiLetterUpper(c) && c != '_')) ||
                !Enum.IsDefined(ticket.Mode) || ticket.ShippedOnly && (string.IsNullOrWhiteSpace(ticket.Inn) || ticket.Inn.Length > 12 || ticket.Inn.Any(c => !char.IsAsciiDigit(c))))
                return false;
            profileId = ticket.ProfileId;
            jobId = ticket.JobId;
            statuses = ticket.Statuses;
            shippedOnly = ticket.ShippedOnly; inn = ticket.Inn; mode = ticket.Mode;
            return true;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("bulk-code-download-v1").ToTimeLimitedDataProtector();
    public string Issue(Guid profileId, Guid jobId, IReadOnlyCollection<string> statuses) =>
        Issue(profileId, jobId, statuses, false, "", CrptIntegrationMode.Mock);
    public bool TryRead(string token, out Guid profileId, out Guid jobId, out string[] statuses) =>
        TryRead(token, out profileId, out jobId, out statuses, out _, out _, out _);
    private sealed record Ticket(Guid ProfileId, Guid JobId, string[] Statuses, bool ShippedOnly = false, string? Inn = null, CrptIntegrationMode Mode = CrptIntegrationMode.Mock);
}
