using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Microsoft.AspNetCore.Components;

namespace CrptWorkbench.Web.Components.Pages;

public partial class ApiConsole
{
    [Inject] private BulkShipmentFileService ShipmentFiles { get; set; } = null!;
    private bool _bulkShippedOnly;
    private bool _shipmentBusy;
    private string? _shipmentError;
    private string? _shipmentInn;
    private int _shipmentVersion;
    private long _shipmentRevision;
    private CancellationTokenSource? _shipmentCancellation;
    private Task? _shipmentTask;
    private readonly Dictionary<Guid, BulkShipmentSummary> _fileShipments = [];

    private long TextShipmentReviewed => _codeResults?.FoundCount ?? 0;
    private long TextShipmentUnknown => string.IsNullOrWhiteSpace(_shipmentInn) ? TextShipmentReviewed : 0;

    private void StopShipmentCheck(bool clearText = false, bool clearFiles = false)
    {
        _shipmentVersion++;
        _shipmentCancellation?.Cancel();
        _shipmentCancellation = null;
        _shipmentTask = null;
        _shipmentBusy = false;
        _shipmentError = null;
        if (clearFiles) { _fileShipments.Clear(); _shipmentInn = null; }
        _shipmentRevision++;
    }

    private void SetBulkShippedOnly(bool selected)
    {
        _bulkShippedOnly = selected;
        StopShipmentCheck(clearText: true, clearFiles: true);
        RefreshBulkSelection();
        StartShipmentCheck();
    }

    private void RetryShipmentCheck()
    {
        StopShipmentCheck(clearText: true, clearFiles: true);
        StartShipmentCheck();
    }

    private void StartShipmentCheck()
    {
        if (_disposed || !_bulkShippedOnly || _running ||
            _selected?.Id != TrueApiBulkCodeInfoHandler.OperationId || Workbench.ProfileId is not { } profileId ||
            _shipmentTask is { IsCompleted: false }) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_bulkPollCancellation.Token);
        _shipmentCancellation = cancellation;
        _shipmentTask = RunShipmentCheckAsync(profileId, _shipmentVersion, cancellation);
    }

    private async Task RunShipmentCheckAsync(Guid profileId, int version, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        bool Current() => !_disposed && version == _shipmentVersion && profileId == Workbench.ProfileId;
        try
        {
            var profile = (await Profiles.ListAsync(token)).FirstOrDefault(p => p.Id == profileId);
            if (!Current() || profile is null) return;
            if (_shipmentInn != profile.Inn?.Trim()) _fileShipments.Clear();
            _shipmentInn = profile.Inn?.Trim() ?? "";
            _shipmentError = null;
            if (string.IsNullOrWhiteSpace(_shipmentInn))
            {
                _shipmentError = "Для фильтра «Отгружен» укажите ИНН активного профиля.";
                RefreshBulkSelection();
                return;
            }
            _shipmentBusy = true;
            RefreshBulkSelection();
            foreach (var job in _bulkJobs.Where(j => j.Mode == IntegrationOptions.Value.Mode && j.State is not ("Ready" or "Deleting") && !_bulkDeleting.Contains(j.Id)).ToArray())
            {
                token.ThrowIfCancellationRequested();
                if (!Current()) return;
                var summary = await ShipmentFiles.GetOwnershipSummaryAsync(profileId, job.Id, token);
                if (!Current()) return;
                _fileShipments[job.Id] = summary;
            }
            _shipmentRevision++;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (Current()) _shipmentError = "Не удалось прочитать сведения о владельцах кодов. Повторите проверку.";
        }
        finally
        {
            cancellation.Dispose();
            if (Current())
            {
                _shipmentCancellation = null;
                _shipmentBusy = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }
}
