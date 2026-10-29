using System.Reflection;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using CrptWorkbench.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class UiProfileAndHistoryRegressionTests
{
    [Fact]
    public async Task CurrentProfileSaveStillPublishesSavedSelection()
    {
        var profile = new CrptProfile { Name = "Saved organization" };
        var repository = new Profiles(profile, false);
        var workbench = new WorkbenchContext();
        var settings = CreateSettings(repository, workbench);
        var save = (Task<bool>)Invoke(settings, "SaveProfileAsync")!;
        await repository.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        repository.Release.SetResult();

        Assert.True(await save);
        Assert.Equal(profile.Id, workbench.ProfileId);
        Assert.Equal(profile.Id, Get<CrptProfile>(settings, "_profile").Id);
        Assert.NotNull(Get<string?>(settings, "_saveMessage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileSavePreservesNewEditorWhenSelectionChangesDuringPersistence(bool blockList)
    {
        var profile = new CrptProfile { Name = "Saved organization" };
        var repository = new Profiles(profile, blockList);
        var workbench = new WorkbenchContext { ProfileId = profile.Id };
        var settings = CreateSettings(repository, workbench);
        var save = (Task<bool>)Invoke(settings, "SaveProfileAsync")!;
        await repository.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Invoke(settings, "NewProfile");
        var newEditor = Get<CrptProfile>(settings, "_profile");
        repository.Release.SetResult();

        Assert.False(await save);
        Assert.Same(newEditor, Get<CrptProfile>(settings, "_profile"));
        Assert.Equal(profile.Id, workbench.ProfileId);
        Assert.Null(Get<string?>(settings, "_saveMessage"));
        Assert.Equal(profile.Id, repository.SavedId);
    }

    [Fact]
    public async Task TraceXSaveStopsWhenProfileEditorChangesDuringProfileSave()
    {
        var profile = new CrptProfile { Name = "Saved organization" };
        var repository = new Profiles(profile, false);
        var secrets = new Secrets();
        var settings = CreateSettings(repository, new WorkbenchContext { ProfileId = profile.Id }, secrets);
        Set(settings, "_traceXConnectionInput", "Server=localhost;Database=TraceX;Integrated Security=true");
        var save = (Task<bool>)Invoke(settings, "SaveTraceXAsync")!;
        await repository.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Invoke(settings, "NewProfile");
        repository.Release.SetResult();

        Assert.False(await save);
        Assert.Empty(secrets.Values);
        Assert.False(Get<bool>(settings, "_traceXConfigured"));
    }

    [Fact]
    public async Task SavedCodeVsdActionOpensMercuryAndPreservesHistoryReturnLink()
    {
        var profile = Guid.NewGuid();
        var log = new ApiRequestLog
        {
            ProfileId = profile, OperationId = TrueApiCodeInfoHandler.OperationId, Success = true,
            ResponseBody = "[{\"cisInfo\":{\"requestedCis\":\"saved-code\",\"cis\":\"saved-code\",\"vsd\":\"8332-6981-984B-455F-881A-A48E-7C9A-3586\"}}]"
        };
        var navigation = new TestNavigation($"http://localhost/history?id={log.Id}");
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<NavigationManager>(navigation)
            .AddSingleton(new WorkbenchContext { ProfileId = profile })
            .AddSingleton(new HistoryService(new Logs(log)))
            .AddSingleton<IApiOperationRegistry>(new ApiOperationRegistry([]))
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<History>();
            Assert.Contains("Запросить ВСД", System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString()));
            var codes = Assert.Single(activator.Components.OfType<CodeInfoResultView>());
            await codes.OnVsdSelected.InvokeAsync("8332-6981-984B-455F-881A-A48E-7C9A-3586");
        });
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.Equal("/console", new Uri(navigation.Uri).AbsolutePath);
        Assert.Equal("8332-6981-984B-455F-881A-A48E-7C9A-3586", query["vsd"].ToString());
        Assert.Equal($"http://localhost/history?id={log.Id}", query["returnTo"].ToString());
    }

    private static Settings CreateSettings(Profiles repository, WorkbenchContext workbench, Secrets? secrets = null)
    {
        var options = new CrptIntegrationOptions();
        var settings = new Settings();
        Property(settings, "ProfileService", new ProfileService(repository, secrets ?? new Secrets(),
            new Certificates(), new Tokens(), new TraceXTester(), new TraceXConnectionStringValidator(), new SensitiveDataMasker(), options));
        Property(settings, "Workbench", workbench);
        Property(settings, "IntegrationOptions", Options.Create(options));
        Property(settings, "Logger", NullLogger<Settings>.Instance);
        Set(settings, "_profile", repository.Profile);
        return settings;
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Invoke(object instance, string method) => instance.GetType().GetMethod(method, Flags)!.Invoke(instance, null);
    private static T Get<T>(object instance, string name) => (T)instance.GetType().GetField(name, Flags)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Flags)!.SetValue(instance, value);
    private static void Property(object instance, string name, object value) => instance.GetType().GetProperty(name, Flags)!.SetValue(instance, value);

    private sealed class Profiles(CrptProfile profile, bool blockList) : IProfileRepository
    {
        public CrptProfile Profile { get; } = profile;
        public Guid? SavedId { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<CrptProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            if (blockList) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return [Profile];
        }
        public Task<CrptProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<CrptProfile?>(id == Profile.Id ? Profile : null);
        public async Task UpsertAsync(CrptProfile value, CancellationToken cancellationToken = default)
        {
            SavedId = value.Id;
            if (!blockList) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
        }
    }
    private sealed class Secrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Values.Remove(key); return Task.CompletedTask; }
    }
    private sealed class Certificates : ICertificateService
    {
        public Task<IReadOnlyList<CertificateInfo>> GetAvailableCertificatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CertificateInfo>>([]);
        public Task<CertificateInfo?> GetCertificateAsync(string thumbprint, CancellationToken cancellationToken = default) => Task.FromResult<CertificateInfo?>(null);
        public Task<CertificateValidationResult> ValidateAsync(string thumbprint, CancellationToken cancellationToken = default) => Task.FromResult(new CertificateValidationResult(false, false, false, "Not configured"));
    }
    private sealed class Tokens : ITrueApiTokenProvider
    {
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Invalidate(IntegrationAuthenticationContext authentication) { }
    }
    private sealed class TraceXTester : ITraceXConnectionTester
    {
        public Task<TraceXConnectionCheckResult> CheckAsync(TraceXDatabaseProvider provider, string connectionString, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize("http://localhost/", uri);
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
    private sealed class CapturingActivator : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            Components.Add(component);
            return component;
        }
    }
    private sealed class Logs(ApiRequestLog log) : IApiRequestLogRepository
    {
        public Task AddAsync(ApiRequestLog entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApiRequestLog>> ListAsync(Guid profileId, int take, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ApiRequestLog>>([log]);
        public Task<ApiRequestLog?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ApiRequestLog?>(profileId == log.ProfileId && id == log.Id ? log : null);
        public Task<RecordPage<ApiRequestLogSummary>> ListSummaryPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecordPage<ApiRequestLogSummary>([new(log.Id, log.CreatedAt, log.CreatedAtUnixMs, log.System, log.OperationId, log.StatusCode, log.DurationMs, log.Success)], null, 1));
    }
}
