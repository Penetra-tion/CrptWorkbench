using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class DemoOperationTests
{
    [Fact]
    public async Task InvalidOrderIdIsRejectedBeforeCallingClient()
    {
        var client = new RecordingSuzClient();
        var handler = new SuzDemoHandler(client);
        await Assert.ThrowsAsync<InputValidationException>(() => handler.ExecuteAsync(
            new Dictionary<string, string> { ["value"] = "bad\nvalue" },
            new(new(), "invalid-order"), TestContext.Current.CancellationToken));
        Assert.False(client.Called);
    }

    [Fact]
    public async Task OtherLookupsRetainEncodedGetAndRequiredSingleLineInput()
    {
        IApiOperationHandler[] handlers = [new SuzDemoHandler(new MockSuzClient())];
        foreach (var handler in handlers)
        {
            Assert.Equal("GET", handler.Descriptor.HttpMethod);
            var field = Assert.Single(handler.Descriptor.Fields);
            Assert.Equal("value", field.Name);
            Assert.True(field.Required);
            Assert.False(field.Multiline);
            var exchange = await handler.ExecuteAsync(new Dictionary<string, string> { ["value"] = "test & value" },
                new(new(), "get-test"), TestContext.Current.CancellationToken);
            Assert.EndsWith("?value=test%20%26%20value", exchange.RequestUrl);
            Assert.Null(exchange.RequestBody);
            Assert.Equal("true", exchange.ResponseHeaders["X-CRPT-Workbench-Mock"]);
            using var json = JsonDocument.Parse(exchange.ResponseBody!);
            Assert.Equal("test & value", json.RootElement.GetProperty("id").GetString());
        }
    }

    private sealed class RecordingSuzClient : ISuzClient
    {
        public bool Called { get; private set; }
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new InvalidOperationException("Client should not be called.");
        }
    }
}
