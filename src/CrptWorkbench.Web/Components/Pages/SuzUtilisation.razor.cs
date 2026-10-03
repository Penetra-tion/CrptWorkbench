using System.Text;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace CrptWorkbench.Web.Components.Pages;

public partial class SuzUtilisation
{
    private const string FilterKey = "crpt-workbench-suz-utilisation-favorite-groups-only";
    private readonly HashSet<string> _favorites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _common = new(StringComparer.Ordinal);
    private readonly List<ConditionInput> _conditions = [];
    private readonly List<RowInput> _rows = [];
    [Microsoft.AspNetCore.Components.Inject] private ISuzCodeSpreadsheetReader SpreadsheetReader { get; set; } = default!;
    private string _group = "milk", _packageType = "UNIT", _scenario = "UTILISATION";
    private bool _favoriteGroupsOnly, _savingFavorite, _busy, _disposed, _loadingJobs;
    private bool _clearEditor;
    private int _version, _jobLoadVersion, _detailVersion, _visibleRows = 100, _visibleReports = 100;
    private string? _message;
    private SuzUtilisationPreparation? _preparation;
    private IReadOnlyList<SuzUtilisationJob> _jobs = [];
    private RecordPageCursor? _jobCursor;
    private long? _jobTotal;
    private SuzUtilisationJobDetails? _details;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private Task? _polling;
    private IJSObjectReference? _module;
    private DotNetObjectReference<SuzUtilisation>? _reference;
    private bool IsFavorite => _favorites.Contains(_group);
    private SuzUtilisationGroup SelectedGroup => SuzUtilisationRules.Groups.First(x => x.Code == _group);
    private IEnumerable<SuzUtilisationGroup> VisibleGroups => SuzUtilisationRules.Groups.Where(x => !_favoriteGroupsOnly || _favorites.Contains(x.Code));
    private IReadOnlyList<SuzUtilisationField> CommonFields => SuzUtilisationRules.Fields(_group, _packageType, _scenario)
        .Where(x => x.Code is not ("kpp" or "fiasId")).ToArray();
    private IReadOnlyList<SuzUtilisationField> IndividualFields => _scenario == "UTILISATION" ? SuzUtilisationRules.IndividualFields(_group, _packageType) : [];
    private sealed class RowInput(string code, IReadOnlyDictionary<string, string> values)
    {
        public string Code { get; } = code;
        public Dictionary<string, string> Values { get; } = new(values, StringComparer.Ordinal);
    }
    private sealed class ConditionInput { public string Index { get; set; } = ""; public string Date { get; set; } = ""; }

    protected override void OnInitialized() { Workbench.ProfileChanged += ProfileChanged; SetDefaults(); }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            if (_module is not null && !SelectedGroup.Automatic)
            {
                await _module.InvokeVoidAsync("attachText", "utilisation-text", _reference);
                if (_clearEditor) { await _module.InvokeVoidAsync("setText", "utilisation-text", ""); _clearEditor = false; }
            }
            return;
        }
        _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./js/suz-utilisation.js");
        _reference = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("attachText", "utilisation-text", _reference);
        _favoriteGroupsOnly = await JavaScript.InvokeAsync<bool>("crptWorkbench.getLocalFlag", FilterKey);
        await LoadFavoritesAsync();
        await LoadJobsAsync(false);
        if (_disposed) return;
        StateHasChanged();
        _polling = PollAsync();
    }

    private SuzUtilisationRequest Request() => new(_group, _packageType, _scenario,
        new Dictionary<string, string>(_common), _rows.Select(x => new SuzUtilisationRow(x.Code, new Dictionary<string, string>(x.Values))).ToArray());
    private string CommonValue(string code) => _common.GetValueOrDefault(code, "");
    private void Invalidate() { _preparation = null; _message = null; }
    [JSInvokable] public Task TextChanged() => InvokeAsync(() => { _rows.Clear(); _visibleRows = 100; Invalidate(); StateHasChanged(); });
    private void GroupChanged()
    {
        _rows.Clear(); _visibleRows = 100; _clearEditor = true;
        _packageType = SuzUtilisationRules.PackageTypes(_group).FirstOrDefault()?.Code ?? "UNIT";
        _scenario = SuzUtilisationRules.Scenarios(_group).FirstOrDefault()?.Code ?? "UTILISATION";
        ContextChanged();
    }
    private void ContextChanged()
    {
        _common.Clear(); _conditions.Clear();
        SetDefaults();
        foreach (var row in _rows) row.Values.Clear();
        Invalidate();
    }
    private void SetDefaults()
    {
        if (SuzUtilisationRules.Fields(_group, _packageType, _scenario).Any(x => x.Code == "usedInProduction")) _common["usedInProduction"] = "1";
    }
    private void SetCommon(string code, ChangeEventArgs e) { _common[code] = e.Value?.ToString() ?? ""; Invalidate(); }
    private void SetIndividual(RowInput row, string code, ChangeEventArgs e) { row.Values[code] = e.Value?.ToString() ?? ""; Invalidate(); }
    private void AddCondition() { _conditions.Add(new()); SyncConditions(); }
    private void RemoveCondition(ConditionInput input) { _conditions.Remove(input); SyncConditions(); }
    private void SetConditionIndex(ConditionInput input, ChangeEventArgs e) { input.Index = e.Value?.ToString() ?? ""; SyncConditions(); }
    private void SetConditionDate(ConditionInput input, ChangeEventArgs e) { input.Date = e.Value?.ToString() ?? ""; SyncConditions(); }
    private void SyncConditions()
    {
        // Keep duplicate/empty indices visible as errors instead of silently replacing a condition.
        _common["expDateConditions"] = _conditions.Count == 0 ? "" : "{" + string.Join(",", _conditions.Select(x => JsonSerializer.Serialize(x.Index) + ":" + JsonSerializer.Serialize(x.Date))) + "}";
        Invalidate();
    }
    private IReadOnlyList<string> RowErrors(int number) => _preparation?.Errors.Where(x => x.RowNumber == number).Select(x => x.Message).ToArray() ?? [];
    private Task Prepare() => RunAsync(async (version, token) => { if (Current(version)) await PrepareAsync(token); });
    private async Task PrepareAsync(CancellationToken token)
    {
        var version = _version;
        var profileId = Workbench.ProfileId;
        var preparation = profileId is { } id
            ? await Utilisation.PrepareAsync(id, Request(), token)
            : Utilisation.Prepare(Request());
        if (!Current(version) || Workbench.ProfileId != profileId) return;
        _preparation = preparation;
        _message = null;
    }
    private async Task ParseAsync(IReadOnlyList<SuzUtilisationRow> rows, CancellationToken token)
    {
        _rows.Clear();
        _rows.AddRange(rows.Select(x => new RowInput(x.Code, x.IndividualValues)));
        _visibleRows = 100;
        await PrepareAsync(token);
    }
    private async Task RemoveRow(RowInput row)
    {
        _rows.Remove(row); await PrepareAsync(_lifetime.Token);
        if (_module is not null)
        {
            var text = string.Join('\n', _rows.Select(x => x.Code));
            await _module.InvokeVoidAsync("setText", "utilisation-text", text);
        }
    }
    private async Task<string> ReadEditorAsync(CancellationToken token)
    {
        if (_module is null) return "";
        await using var reference = await _module.InvokeAsync<IJSStreamReference>("readText", "utilisation-text");
        await using var stream = await reference.OpenReadStreamAsync(long.MaxValue, token);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return await reader.ReadToEndAsync(token);
    }
    private Task ReadTextAsync() => RunAsync(async (version, token) =>
    {
        var text = await ReadEditorAsync(token);
        var rows = SuzUtilisationInput.ParseText(text);
        if (Current(version)) await ParseAsync(rows, token);
    });
    private Task ReadFileAsync(InputFileChangeEventArgs e) => RunAsync(async (version, token) =>
    {
        var extension = Path.GetExtension(e.File.Name).ToLowerInvariant();
        if (extension is not (".txt" or ".csv" or ".xlsx")) throw new FormatException("Поддерживаются только TXT, CSV и XLSX.");
        await using var stream = e.File.OpenReadStream(long.MaxValue, token);
        IReadOnlyList<SuzUtilisationRow> rows;
        if (extension == ".xlsx") rows = SuzUtilisationInput.Codes(await SpreadsheetReader.ReadFirstColumnAsync(stream, token));
        else
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
            var content = await reader.ReadToEndAsync(token);
            rows = extension == ".csv" ? SuzUtilisationInput.ParseCsv(content) : SuzUtilisationInput.ParseText(content);
        }
        if (!Current(version)) return;
        if (_module is not null) await _module.InvokeVoidAsync("setText", "utilisation-text", string.Join('\n', rows.Select(x => x.Code)));
        if (Current(version)) await ParseAsync(rows, token);
    });
    private Task ApplyTraceXAsync((string Text, bool Append) import) => RunAsync(async (version, token) =>
    {
        var existing = import.Append ? await ReadEditorAsync(token) : "";
        if (!Current(version)) return;
        var text = string.IsNullOrWhiteSpace(existing) ? import.Text : existing.TrimEnd('\r', '\n') + "\n" + import.Text;
        if (_module is not null) await _module.InvokeVoidAsync("setText", "utilisation-text", text);
        var rows = SuzUtilisationInput.ParseText(text);
        if (Current(version)) await ParseAsync(rows, token);
    });
    private async Task DownloadTemplateAsync()
    {
        if (_module is not null) await _module.InvokeVoidAsync("downloadText", "Нанесение-" + _group + ".csv", SuzUtilisationInput.TemplateCsv());
    }
    private async Task UseMockExampleAsync()
    {
        if (_busy || IntegrationOptions.Value.Mode != CrptIntegrationMode.Mock || _module is null) return;
        var version = _version;
        _group = "milk"; _packageType = "UNIT"; _scenario = "UTILISATION";
        ContextChanged();
        _common["productionDate"] = DateTimeOffset.Now.AddDays(-1).ToString("yyyy-MM-dd");
        _common["expDate"] = DateTimeOffset.Now.AddDays(7).ToString("yyyy-MM-dd");
        const string text = "0104640016748389215fp(IS\u001D93dGVz\n0104640016748389215sm8zf\u001D93dGVz";
        await _module.InvokeVoidAsync("setText", "utilisation-text", text);
        if (Current(version)) await ParseAsync(SuzUtilisationInput.ParseText(text), _lifetime.Token);
    }
    private Task SubmitAsync() => RunAsync(async (version, token) =>
    {
        if (Workbench.ProfileId is not { } id) return;
        var request = Request();
        _preparation = null;
        var result = await Utilisation.SubmitAsync(id, request, token);
        if (!Current(version)) return;
        _preparation = null;
        _details = result; _visibleReports = 100;
        _message = "Результаты отправки сохранены. Статусы и квитанции обновляются автоматически.";
        await ReloadJobsAsync();
    });
    private Task ContinueAsync(Guid jobId) => RunAsync(async (version, token) =>
    {
        if (Workbench.ProfileId is not { } id) return;
        var result = await Utilisation.ContinueAsync(id, jobId, token);
        if (Current(version)) { _details = result; _message = "Отправка оставшихся частей выполнена. Проверьте результаты каждого отчёта."; }
    });
    private Task ScheduleAsync(Guid reportId, bool resume) => RunAsync(async (version, token) =>
    {
        if (Workbench.ProfileId is not { } id) return;
        if (resume) await Utilisation.ResumeAsync(id, reportId, token);
        else await Utilisation.CheckNowAsync(id, reportId, token);
        if (Current(version)) _message = resume ? "Отслеживание возобновлено." : "Проверка запланирована.";
    });

    private async Task RunAsync(Func<int, CancellationToken, Task> action)
    {
        if (_busy || _disposed) return;
        var version = _version;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = cancellation; _busy = true; _message = null;
        try { await action(version, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (Current(version)) { _message = "Операция остановлена. Полученные GUID сохранены; проверьте результаты перед новой отправкой."; await ReloadJobsAsync(); await RefreshDetailsAsync(); }
        }
        catch (DecoderFallbackException)
        {
            if (Current(version)) _message = "Файл TXT или CSV должен быть в кодировке UTF-8.";
        }
        catch (ArgumentException)
        {
            if (Current(version)) _message = "Данные не прошли проверку. Проверьте поля и строки перед отправкой.";
        }
        catch (FormatException ex)
        {
            if (Current(version)) _message = ex.Message;
        }
        catch (Exception)
        {
            if (Current(version)) { _message = "Не удалось завершить операцию. Проверьте сохранённые отчёты перед повторной отправкой."; await ReloadJobsAsync(); }
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation)) _operationCancellation = null;
            if (Current(version)) _busy = false;
        }
    }
    private bool Current(int version) => !_disposed && version == _version;
    private void Cancel() => _operationCancellation?.Cancel();

    private void NormalizeFavoriteSelection()
    {
        if (_favorites.Count == 0) _favoriteGroupsOnly = false;
        if (_favoriteGroupsOnly && !IsFavorite) { _group = VisibleGroups.First().Code; GroupChanged(); }
    }
    private async Task LoadFavoritesAsync()
    {
        var version = _version;
        var profile = Workbench.ProfileId;
        try
        {
            var values = profile is { } id ? await FavoriteGroups.ListAsync(id, _lifetime.Token) : [];
            if (!Current(version) || Workbench.ProfileId != profile) return;
            _favorites.Clear();
            _favorites.UnionWith(values.Where(x => SuzUtilisationRules.Groups.Any(g => g.Code == x)));
            NormalizeFavoriteSelection();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (Current(version)) _message = "Не удалось загрузить избранные товарные группы."; }
    }
    private async Task SetFavoriteFilterAsync(bool value)
    {
        if (_busy) return;
        _favoriteGroupsOnly = value; NormalizeFavoriteSelection();
        if (!value && IsFavorite && SuzUtilisationRules.Groups.FirstOrDefault(x => !_favorites.Contains(x.Code)) is { } other) { _group = other.Code; GroupChanged(); }
        await JavaScript.InvokeVoidAsync("crptWorkbench.setLocalFlag", FilterKey, _favoriteGroupsOnly);
    }
    private async Task ToggleFavoriteAsync()
    {
        if (_busy || _savingFavorite || Workbench.ProfileId is not { } id) return;
        var version = _version; var group = _group; var favorite = !IsFavorite;
        _savingFavorite = true;
        try
        {
            await FavoriteGroups.SetAsync(id, group, favorite, _lifetime.Token);
            if (!Current(version)) return;
            if (favorite) _favorites.Add(group); else _favorites.Remove(group);
            NormalizeFavoriteSelection();
            await JavaScript.InvokeVoidAsync("crptWorkbench.setLocalFlag", FilterKey, _favoriteGroupsOnly);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (Current(version)) _message = "Не удалось сохранить избранную товарную группу."; }
        finally { if (Current(version)) _savingFavorite = false; }
    }

    private Task ReloadJobsAsync() { _jobs = []; _jobCursor = null; _jobTotal = null; ++_jobLoadVersion; _loadingJobs = false; return LoadJobsAsync(false); }
    private Task LoadMoreJobsAsync() => LoadJobsAsync(true);
    private async Task LoadJobsAsync(bool append)
    {
        if (_loadingJobs || _disposed || (append && _jobCursor is null)) return;
        var context = _version; var load = ++_jobLoadVersion; var profile = Workbench.ProfileId;
        _loadingJobs = true;
        try
        {
            if (profile is not { } id) { _jobs = []; return; }
            if (!append && _jobs.Count > 0)
            {
                var items = await Utilisation.GetManyAsync(id, _jobs.Select(x => x.Id).ToArray(), _lifetime.Token);
                if (Current(context) && load == _jobLoadVersion) _jobs = items;
            }
            else
            {
                var page = await Utilisation.ListPageAsync(id, append ? _jobCursor : null, _lifetime.Token);
                if (!Current(context) || load != _jobLoadVersion) return;
                _jobs = append ? _jobs.Concat(page.Items).DistinctBy(x => x.Id).ToArray() : page.Items;
                _jobCursor = page.NextCursor; _jobTotal = page.TotalCount;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (Current(context) && load == _jobLoadVersion) _message = "Не удалось обновить список отчётов."; }
        finally { if (load == _jobLoadVersion) _loadingJobs = false; }
    }
    private async Task OpenJobAsync(Guid jobId)
    {
        if (_details?.Job.Id == jobId) { ++_detailVersion; _details = null; return; }
        _visibleReports = 100;
        await ReadDetailsAsync(jobId);
    }
    private Task RefreshDetailsAsync() => _details is { } details ? ReadDetailsAsync(details.Job.Id) : Task.CompletedTask;
    private async Task ReadDetailsAsync(Guid jobId)
    {
        if (Workbench.ProfileId is not { } id) return;
        var version = _version; var load = ++_detailVersion;
        try
        {
            var details = await Utilisation.GetAsync(id, jobId, _lifetime.Token);
            if (Current(version) && load == _detailVersion) _details = details;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (Current(version) && load == _detailVersion) _message = "Не удалось обновить результаты задания."; }
    }
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                await InvokeAsync(async () => { if (_busy || _disposed) return; await LoadJobsAsync(false); await RefreshDetailsAsync(); StateHasChanged(); });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private void ProfileChanged(object? sender, Guid? id) => _ = InvokeAsync(async () =>
    {
        ++_version; ++_detailVersion; ++_jobLoadVersion; Cancel(); _busy = false; _savingFavorite = false; _loadingJobs = false;
        _rows.Clear(); _details = null; _jobs = []; _jobCursor = null; _jobTotal = null; _favorites.Clear(); ContextChanged();
        if (_module is not null) await _module.InvokeVoidAsync("setText", "utilisation-text", "");
        await LoadFavoritesAsync(); await LoadJobsAsync(false); StateHasChanged();
    });
    private static string GroupName(string group) => SuzUtilisationRules.Groups.FirstOrDefault(x => x.Code == group)?.Name ?? group;
    private static string PackageName(string type) => type switch { "UNIT" => "Единицы товара", "GROUP" => "Групповые упаковки", "SET" => "Наборы", _ => type };
    private static string DisplayCode(string code) => code.Replace("\u001D", "␝", StringComparison.Ordinal);
    private static string Date(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    private static string ReceiptUrl(string reportId) => OperationNavigation.DocumentUrl(new(reportId, ApiSystem.Suz, null));
    private Task ScheduleReportAsync(SuzUtilisationReport report) => ScheduleAsync(report.Id, report.State == "Paused");
    private static string ReportStatus(SuzUtilisationReport report) => report.State switch
    {
        "Prepared" => "Не отправлен", "Sending" => "Отправляется", "Unknown" => "Исход отправки неизвестен",
        "Rejected" => "Отклонён", "Tracking" => "Обрабатывается", "Paused" => "Отслеживание приостановлено",
        "Finished" when report.ReceiptState == "SUCCESS" => "Нанесение завершено",
        "Finished" when report.ReceiptState is "FAILED" or "PARTIALLY" || report.RemoteStatus == "REJECTED" => "Завершён с ошибкой",
        "Finished" => "Обработка завершена", _ => report.State
    };
    private static string ReportTone(SuzUtilisationReport report) => report.State is "Rejected" or "Unknown" || report.ReceiptState is "FAILED" or "PARTIALLY" || report.RemoteStatus == "REJECTED" ? "tone-error" : report.ReceiptState == "SUCCESS" ? "tone-success" : "tone-warning";
    private static string Overall(SuzUtilisationJobDetails details)
    {
        var parts = details.Reports;
        var success = parts.Count(x => x.State == "Finished" && x.ReceiptState == "SUCCESS");
        var pending = parts.Count(x => x.State == "Prepared");
        var failed = parts.Count(x => x.State is "Rejected" or "Unknown" || x.ReceiptState is "FAILED" or "PARTIALLY" || x.RemoteStatus == "REJECTED");
        return $"Завершено успешно: {success} из {parts.Count}. Требуют внимания: {failed}. Не отправлено: {pending}.";
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; Workbench.ProfileChanged -= ProfileChanged;
        await _lifetime.CancelAsync(); Cancel();
        if (_polling is not null) await _polling;
        if (_module is not null)
        {
            try { await _module.InvokeVoidAsync("detachText", "utilisation-text"); await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _reference?.Dispose(); _lifetime.Dispose();
    }
}
