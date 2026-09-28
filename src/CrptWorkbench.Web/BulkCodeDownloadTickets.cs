using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace CrptWorkbench.Web;

public sealed class BulkCodeDownloadTickets(IDataProtectionProvider protection)
{
    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("bulk-code-download-v1").ToTimeLimitedDataProtector();

    public string Issue(Guid profileId, Guid jobId, IReadOnlyCollection<string> statuses) =>
        _protector.Protect(JsonSerializer.Serialize(new Ticket(profileId, jobId, statuses.ToArray())), TimeSpan.FromMinutes(5));

    public bool TryRead(string token, out Guid profileId, out Guid jobId, out string[] statuses)
    {
        profileId = jobId = Guid.Empty;
        statuses = [];
        try
        {
            var ticket = JsonSerializer.Deserialize<Ticket>(_protector.Unprotect(token));
            if (ticket is null || ticket.ProfileId == Guid.Empty || ticket.JobId == Guid.Empty ||
                ticket.Statuses.Length == 0 || ticket.Statuses.Any(x => x.Length is < 1 or > 40 || x.Any(c => !char.IsAsciiLetterUpper(c) && c != '_')))
                return false;
            profileId = ticket.ProfileId;
            jobId = ticket.JobId;
            statuses = ticket.Statuses;
            return true;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record Ticket(Guid ProfileId, Guid JobId, string[] Statuses);
}
