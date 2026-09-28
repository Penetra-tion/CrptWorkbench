using System.Text.Json;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Xunit;
using static CrptWorkbench.Tests.SuzTransportTests;

namespace CrptWorkbench.Tests;

public sealed class SuzTokenTests
{
    [Fact]
    public async Task TokenSingleFlightExpiryIsolationAndStaleInvalidation()
    {
        var signatures = new Signatures();
        var time = new Clock();
        var issued = 0;
        var authRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory(new Handler(async request => {
            await authRelease.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("correlation", request.Headers.GetValues("X-Correlation-Id").Single());
            if (request.Method == HttpMethod.Get) return await Reply(200, "{\"uuid\":\"challenge-id\",\"data\":\" исходные данные \\n\"}");
            Assert.Contains("/auth/simpleSignIn/", request.RequestUri!.AbsolutePath);
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("attached-signature", body.RootElement.GetProperty("data").GetString());
            Assert.Matches("^[0-9]{10}$", body.RootElement.GetProperty("inn").GetString()!);
            return await Reply(200, "{\"token\":\"opaque-" + Interlocked.Increment(ref issued) + "\"}");
        }));
        var provider = new SuzTokenProvider(factory, signatures, OptionsFor(), time);
        var pending = Enumerable.Range(0, 12).Select(_ => provider.GetTokenAsync(Auth, Connection, "correlation", TestContext.Current.CancellationToken)).ToArray();
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        Assert.Equal(0, issued);
        authRelease.SetResult();
        var tokens = await Task.WhenAll(pending);
        Assert.All(tokens, token => Assert.Equal("opaque-1", token));
        Assert.Equal(1, issued);
        Assert.Equal(" исходные данные \n", signatures.Data);
        provider.Invalidate(Auth, Connection, "opaque-1");
        Assert.Equal("opaque-2", await provider.GetTokenAsync(Auth, Connection, "correlation", TestContext.Current.CancellationToken));
        provider.Invalidate(Auth, Connection, "opaque-1");
        Assert.Equal("opaque-2", await provider.GetTokenAsync(Auth, Connection, "correlation", TestContext.Current.CancellationToken));
        time.Now = time.Now.AddHours(9);
        Assert.Equal("opaque-3", await provider.GetTokenAsync(Auth, Connection, "correlation", TestContext.Current.CancellationToken));
        await provider.GetTokenAsync(Auth with { ProfileId = Guid.NewGuid() }, Connection, "correlation", TestContext.Current.CancellationToken);
        await provider.GetTokenAsync(Auth with { CertificateThumbprint = new string('B', 40) }, Connection, "correlation", TestContext.Current.CancellationToken);
        await provider.GetTokenAsync(Auth, Connection with { ConnectionId = "another" }, "correlation", TestContext.Current.CancellationToken);
        await provider.GetTokenAsync(Auth, Connection with { OmsId = Guid.NewGuid().ToString() }, "correlation", TestContext.Current.CancellationToken);
        Assert.Equal(7, issued);
        await provider.GetTokenAsync(Auth with { Inn = "7800000000" }, Connection, "correlation", TestContext.Current.CancellationToken);
        Assert.Equal(8, issued);
        Assert.Equal("opaque-3", await provider.GetTokenAsync(Auth, Connection with { BaseUrl = "https://suzgrid.crpt.ru/" }, "correlation", TestContext.Current.CancellationToken));
        Assert.Equal(8, issued);
    }

    [Fact]
    public async Task InvalidModeConnectionAuthAndCancellationHaveNoSideEffects()
    {
        var factory = new Factory(new Handler(_ => throw new Exception("network")));
        var signatures = new Signatures();
        var provider = new SuzTokenProvider(factory, signatures, OptionsFor(), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(Auth, Connection with { ConnectionId = "a/b" }, "x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(Auth with { Inn = "bad" }, Connection, "x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SuzTokenProvider(factory, signatures, OptionsFor(CrptIntegrationMode.Mock), TimeProvider.System).GetTokenAsync(Auth, Connection, "x", TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetTokenAsync(Auth, Connection, "x", cancelled.Token));
        Assert.Null(signatures.Data);
    }

    [Theory]
    [InlineData("broken-json")]
    [InlineData("{}")]
    [InlineData("{\"token\":\"bad\\nvalue\"}")]
    public async Task InvalidTokenResponseIsSafeAndNeverCached(string tokenBody)
    {
        var issued = 0;
        var provider = new SuzTokenProvider(new Factory(new Handler(request => {
            if (request.Method == HttpMethod.Get) return Reply(200, "{\"uuid\":\"id\",\"data\":\"data\"}");
            issued++;
            return Reply(200, tokenBody);
        })), new Signatures(), OptionsFor(), TimeProvider.System);
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(Auth, Connection, "x", TestContext.Current.CancellationToken));
        Assert.Equal(2, issued);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignerFailuresAreSanitizedAndTimeoutIsDistinct(bool timeout)
    {
        var provider = new SuzTokenProvider(new Factory(new Handler(_ => Reply(200, "{\"uuid\":\"id\",\"data\":\"data\"}"))),
            new ThrowingSignatures(timeout), OptionsFor(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(Auth, Connection, "x", TestContext.Current.CancellationToken));
        Assert.Equal(timeout ? "Превышено время ожидания авторизации СУЗ." : "Не удалось выполнить авторизацию СУЗ. Проверьте подключение, сертификат и права доступа.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET-SIGNATURE-SENTINEL", error.ToString());
    }

    private sealed class ThrowingSignatures(bool timeout) : ICertificateSignatureService
    {
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken ct = default) =>
            throw (timeout ? new OperationCanceledException("SECRET-SIGNATURE-SENTINEL") : new InvalidOperationException("SECRET-SIGNATURE-SENTINEL"));
    }

    private sealed class Signatures : ICertificateSignatureService
    {
        public string? Data;
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken ct = default) { Data = data; return Task.FromResult("attached-signature"); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
