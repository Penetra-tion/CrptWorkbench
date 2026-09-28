using System.Text;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace CrptWorkbench.Web.Components.Pages;

public partial class CodeCorrection
{
    private const string FavoriteGroupsOnlyStorageKey = "crpt-workbench-code-correction-favorite-groups-only";
    private readonly HashSet<string> _favoriteGroupCodes = new(StringComparer.Ordinal);
    private bool _favoriteGroupsOnly, _savingFavorite;
    private bool IsFavoriteGroup => _favoriteGroupCodes.Contains(_group);
    private IEnumerable<CorrectionProductGroup> VisibleGroups => CodeCorrectionRules.Groups.Where(x => !_favoriteGroupsOnly || _favoriteGroupCodes.Contains(x.Code));
    private string _group = "milk", _kind = "productionDate", _value = "";
    private int _operation, _page, _contextVersion, _loadVersion;
    private bool _busy, _disposed, _loadingDocuments, _checkBeforeSending = true;
    private string? _message;
    private string _busyLabel = "";
    private Guid? _checkId;
    private List<string> _codes = [];
    private Dictionary<string, CorrectionCodeRow> _rows = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _errors = [], _serverChecks = [];
    private IReadOnlyList<PackageDocument> _documents = [];
    private readonly List<DocumentInput> _documentsInput = [new()];
    private readonly List<ExpirationInput> _expirationInput = [new()];
    private CorrectionCheckProgress? _progress;
    private IJSObjectReference? _module;
    private DotNetObjectReference<CodeCorrection>? _reference;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private sealed class DocumentInput { public int Type { get; set; } = 1; public string Number { get; set; } = ""; public string Date { get; set; } = ""; }
    private sealed class ExpirationInput { public int Index { get; set; } = 1; public string Date { get; set; } = ""; }

    private CodeCorrectionRequest Request() => new(_group, _kind, new(_value, _operation,
        _kind == "permitDocs" ? _documentsInput.Select(x => new CorrectionPermitDocument(x.Type, x.Number, x.Date)).ToArray() : null,
        _kind == "licences" ? _documentsInput.Select(x => new CorrectionLicence(x.Number, x.Date)).ToArray() : null,
        _kind == "variableExpiration" ? _expirationInput.Select(x => new CorrectionExpiration(x.Index, x.Date)).ToArray() : null), _codes.ToArray());
    private bool CanSend => Workbench.ProfileId is { } id && _codes.Count > 0 &&
        (_checkBeforeSending ? Corrections.CanSubmit(id, Request(), _checkId) : Corrections.Validate(Request()).Count == 0);
    protected override void OnInitialized() => Workbench.ProfileChanged += ProfileChanged;
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./js/package-drop.js");
        _reference = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("attachText", "correction-text", _reference);
        _favoriteGroupsOnly = await JavaScript.InvokeAsync<bool>("crptWorkbench.getLocalFlag", FavoriteGroupsOnlyStorageKey);
        await LoadFavoriteGroupsAsync();
        await LoadAsync();
        StateHasChanged();
        _ = RefreshAsync();
    }
    private void Invalidate()
    {
        Corrections.InvalidateCheck();
        _checkId = null; _rows.Clear(); _serverChecks = []; _errors = _codes.Count > 0 ? Corrections.Validate(Request()) : []; _message = null;
    }
    private void GroupChanged()
    {
        if (!CodeCorrectionRules.Supports(_group, _kind)) _kind = CodeCorrectionRules.Kinds.First(x => CodeCorrectionRules.Supports(_group, x.Code)).Code;
        KindChanged();
    }
    private void NormalizeFavoriteSelection()
    {
        if (_favoriteGroupCodes.Count == 0) _favoriteGroupsOnly = false;
        if (_favoriteGroupsOnly && !IsFavoriteGroup)
        {
            _group = VisibleGroups.First().Code;
            GroupChanged();
        }
    }
    private async Task SetFavoriteGroupsOnlyAsync(bool value)
    {
        if (_busy) return;
        _favoriteGroupsOnly = value;
        NormalizeFavoriteSelection();
        if (!_favoriteGroupsOnly && IsFavoriteGroup &&
            CodeCorrectionRules.Groups.FirstOrDefault(x => !_favoriteGroupCodes.Contains(x.Code)) is { } group)
        {
            _group = group.Code;
            GroupChanged();
        }
        await JavaScript.InvokeVoidAsync("crptWorkbench.setLocalFlag", FavoriteGroupsOnlyStorageKey, _favoriteGroupsOnly);
    }
    private async Task LoadFavoriteGroupsAsync()
    {
        var version = _contextVersion;
        _favoriteGroupCodes.Clear();
        try
        {
            var codes = Workbench.ProfileId is { } id ? await FavoriteGroups.ListAsync(id, _lifetime.Token) : [];
            if (!Current(version)) return;
            foreach (var code in codes)
                if (CodeCorrectionRules.Groups.Any(x => x.Code == code)) _favoriteGroupCodes.Add(code);
            NormalizeFavoriteSelection();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception) { if (Current(version)) _message = "Не удалось загрузить избранные товарные группы."; }
    }
    private async Task ToggleFavoriteGroupAsync()
    {
        if (_busy || _savingFavorite || Workbench.ProfileId is not { } id) return;
        var version = _contextVersion;
        var group = _group;
        var favorite = !IsFavoriteGroup;
        _savingFavorite = true;
        try
        {
            await FavoriteGroups.SetAsync(id, group, favorite, _lifetime.Token);
            if (!Current(version)) return;
            if (favorite) _favoriteGroupCodes.Add(group); else _favoriteGroupCodes.Remove(group);
            NormalizeFavoriteSelection();
            await JavaScript.InvokeVoidAsync("crptWorkbench.setLocalFlag", FavoriteGroupsOnlyStorageKey, _favoriteGroupsOnly);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception) { if (Current(version)) _message = "Не удалось сохранить избранную товарную группу."; }
        finally { _savingFavorite = false; }
    }
    private void KindChanged()
    {
        _value = _kind == "emissionType" ? "1" : "";
        _documentsInput.Clear(); _documentsInput.Add(new() { Type = CodeCorrectionRules.AllowedPermitTypes(_group).FirstOrDefault() });
        _expirationInput.Clear(); _expirationInput.Add(new()); _operation = 0; Invalidate();
    }
    private void SetValue(ChangeEventArgs e) { _value = e.Value?.ToString() ?? ""; Invalidate(); }
    private void SetDate(DocumentInput row, ChangeEventArgs e) { row.Date = e.Value?.ToString() ?? ""; Invalidate(); }
    private void SetExpiration(ExpirationInput row, ChangeEventArgs e) { row.Date = e.Value?.ToString() ?? ""; Invalidate(); }
    private void AddDocument() { _documentsInput.Add(new() { Type = CodeCorrectionRules.AllowedPermitTypes(_group).FirstOrDefault() }); Invalidate(); }
    private void RemoveDocument(DocumentInput row) { _documentsInput.Remove(row); Invalidate(); }
    private void AddExpiration() { _expirationInput.Add(new() { Index = _expirationInput.Max(x => x.Index) + 1 }); Invalidate(); }
    private void RemoveExpiration(ExpirationInput row) { _expirationInput.Remove(row); Invalidate(); }
    [JSInvokable] public Task TextChanged() => InvokeAsync(() => { _codes.Clear(); _page = 0; Invalidate(); StateHasChanged(); });
    private void ProfileChanged(object? sender, Guid? id) => _ = InvokeAsync(async () =>
    {
        _contextVersion++; Cancel(); _codes.Clear(); Invalidate(); _documents = [];
        await LoadFavoriteGroupsAsync();
        if (_module is not null) await _module.InvokeVoidAsync("setText", "correction-text", "");
        await LoadAsync(); if (!_disposed) StateHasChanged();
    });
    private void Parse(string text)
    {
        Invalidate();
        var parsed = CodeCorrectionService.ParseCodes(text);
        _codes = parsed.Codes.ToList(); _page = 0;
        _message = $"Загружено кодов: {_codes.Count}. Исключено повторов: {parsed.DuplicateCount}.";
        _errors = Corrections.Validate(Request());
    }
    private Task ReadTextAsync() => RunAsync("Загружаем список…", async (version, token) =>
    {
        if (_module is null) return;
        await using var reference = await _module.InvokeAsync<IJSStreamReference>("readText", "correction-text");
        await using var stream = await reference.OpenReadStreamAsync(long.MaxValue, token);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
        var text = await reader.ReadToEndAsync(token);
        if (Current(version)) Parse(text);
    });
    private Task ReadFileAsync(InputFileChangeEventArgs e) => RunAsync("Читаем TXT…", async (version, token) =>
    {
        await using var stream = e.File.OpenReadStream(long.MaxValue, token);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
        var text = await reader.ReadToEndAsync(token);
        if (!Current(version)) return;
        if (_module is not null) await _module.InvokeVoidAsync("setText", "correction-text", text);
        Parse(text);
    });
    private Task CheckAsync() => RunAsync("Проверяем коды…", async (version, token) =>
    {
        if (Workbench.ProfileId is not { } id) return;
        _checkId = null; _rows.Clear(); _errors = []; _serverChecks = [];
        var progress = new Progress<CorrectionCheckProgress>(value => _ = InvokeAsync(() =>
        {
            if (Current(version) && _busy) { _progress = value; StateHasChanged(); }
        }));
        var result = await Corrections.CheckAsync(id, Request(), progress, token);
        if (!Current(version)) return;
        _checkId = result.CheckId; _rows = result.Rows.ToDictionary(x => x.Code, StringComparer.Ordinal); _serverChecks = result.ServerChecks;
        _message = $"Проверка завершена. Подходят: {result.Rows.Count(x => x.Passed)}; требуют внимания: {result.Rows.Count(x => !x.Passed)}.";
    });
    private async Task RemoveAsync(string code) { _codes.Remove(code); _rows.Remove(code); await SyncRetainedAsync(); }
    private async Task RemoveFailedAsync()
    {
        var failed = _rows.Values.Where(x => !x.Passed).Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        _codes.RemoveAll(failed.Contains); foreach (var code in failed) _rows.Remove(code); await SyncRetainedAsync();
    }
    private async Task SyncRetainedAsync()
    {
        _page = Math.Min(_page, Math.Max(0, (_codes.Count - 1) / 100));
        if (_module is not null) await _module.InvokeVoidAsync("setText", "correction-text", string.Join('\n', _codes));
        _errors = Corrections.Validate(Request()); _message = "Список обновлён. Результаты проверки оставшихся кодов сохранены.";
    }
    private Task SubmitAsync() => RunAsync("Подписываем и отправляем…", async (version, token) =>
    {
        if (Workbench.ProfileId is not { } id || !CanSend) return;
        var document = await Corrections.SubmitAsync(id, Request(), _checkBeforeSending, _checkId, token);
        if (!Current(version)) return;
        _codes.Clear(); Invalidate();
        if (_module is not null) await _module.InvokeVoidAsync("setText", "correction-text", "");
        _message = document.DocumentId is not null ? "Документ отправлен. Статус обновляется автоматически." : "Проверьте результат в списке документов. Не повторяйте отправку при неизвестном результате.";
        await LoadAsync();
    });
    private Task ContinueAsync(PackageDocument document) => RunAsync("Проверяем документ…", async (version, token) =>
    {
        if (Workbench.ProfileId is not { } id) return;
        if (document.State == "Paused") await Corrections.ResumeAsync(id, document.Id, token);
        else await Corrections.CheckNowAsync(id, document.Id, token);
        await DocumentTracker.PollDueAsync(token);
        if (Current(version)) await LoadAsync();
    });
    private bool Current(int version) => !_disposed && version == _contextVersion;
    private void Cancel() => _operationCancellation?.Cancel();
    private async Task RunAsync(string label, Func<int, CancellationToken, Task> action)
    {
        if (_busy || _disposed) return;
        var version = _contextVersion;
        _busy = true; _busyLabel = label; _progress = null; _message = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = cancellation;
        try { await action(version, cancellation.Token); }
        catch (OperationCanceledException) { if (Current(version)) _message = "Операция отменена. Если отправка уже началась, проверьте список документов перед повторной отправкой."; }
        catch (ArgumentException ex) { if (Current(version)) _errors = [ex.Message]; }
        catch (InvalidOperationException) { if (Current(version)) _errors = ["Операцию выполнить не удалось. Проверьте выбранный профиль и список документов перед повторной отправкой."]; }
        catch (Exception) { if (Current(version)) _message = "Не удалось завершить операцию. Проверьте введённые данные и список документов перед повторной отправкой."; }
        finally { _operationCancellation = null; _busy = false; _progress = null; if (!_disposed) await LoadAsync(); }
    }
    private async Task LoadAsync()
    {
        var load = ++_loadVersion; var context = _contextVersion; var profile = Workbench.ProfileId;
        _loadingDocuments = true;
        try
        {
            var documents = profile is { } id ? await Corrections.ListAsync(id, _lifetime.Token) : [];
            if (Current(context) && load == _loadVersion) _documents = documents;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception) { if (Current(context) && load == _loadVersion) _message = "Не удалось обновить документы. Повторите обновление."; }
        finally { if (load == _loadVersion) _loadingDocuments = false; }
    }
    private async Task RefreshAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(_lifetime.Token)) await InvokeAsync(async () => { if (!_busy) await LoadAsync(); StateHasChanged(); });
        }
        catch (OperationCanceledException) { }
    }
    private static string PermitLabel(int type) => type switch { 1 => "Сертификат соответствия", 2 => "Декларация соответствия", 4 => "Свидетельство о госрегистрации", _ => type.ToString() };
    private static string KindName(string kind) => CodeCorrectionRules.Kinds.FirstOrDefault(x => x.Code == kind)?.Name ?? kind;
    private static string GroupName(string? group) => CodeCorrectionRules.Groups.FirstOrDefault(x => x.Code == group || x.Id.ToString() == group)?.Name ?? group ?? "—";
    private static string Date(long? date) => date is null ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(date.Value).ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    private static string StateLabel(PackageDocument doc) => doc.State switch { "Tracking" => "Проверяется", "Finished" => "Обработка завершена", "Paused" => "Отслеживание приостановлено", "Unknown" or "Sending" => "Результат отправки неизвестен", "Rejected" => "Не отправлен", "Prepared" => "Подготовка прервана; не отправлен", _ => doc.State };
    public async ValueTask DisposeAsync()
    {
        _disposed = true; Workbench.ProfileChanged -= ProfileChanged; await _lifetime.CancelAsync();
        if (_module is not null) { try { await _module.InvokeVoidAsync("detachText", "correction-text"); await _module.DisposeAsync(); } catch (JSDisconnectedException) { } }
        _reference?.Dispose(); _lifetime.Dispose();
    }
}
