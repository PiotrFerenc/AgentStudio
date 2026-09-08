using System.Net;
using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public class SecureHttpExecutorTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int SendCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StubFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);
        public HttpClient CreateClient(string name) => _client;
    }

    private static SecureHttpExecutor Create(HttpMessageHandler handler, params string[] allowedHosts)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(allowedHosts
                .Select((h, i) => new KeyValuePair<string, string?>($"HttpTool:AllowedHosts:{i}", h)))
            .Build();
        return new SecureHttpExecutor(new StubFactory(handler), NullLogger<SecureHttpExecutor>.Instance, config);
    }

    private static HttpNode Node(string url) => new() { Id = "http1", Method = "GET", Url = url };

    [Theory]
    [InlineData("http://localhost/api")]
    [InlineData("http://127.0.0.1/api")]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("http://192.168.1.10/internal")]
    [InlineData("http://172.16.0.1/internal")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public async Task PrivateAndLoopbackUrls_AreBlocked_NoRequestSent(string url)
    {
        var handler = new RecordingHandler();
        var executor = Create(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(Node(url), new Dictionary<string, string>()));
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PublicIpLiteral_PassesValidation_RequestIsSent()
    {
        var handler = new RecordingHandler();
        var executor = Create(handler);

        var result = await executor.ExecuteAsync(Node("http://8.8.8.8/dns"), new Dictionary<string, string>());

        Assert.Equal("ok", result);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task AllowlistedInternalHost_IsPermitted()
    {
        var handler = new RecordingHandler();
        var executor = Create(handler, "localhost");

        var result = await executor.ExecuteAsync(Node("http://localhost/api"), new Dictionary<string, string>());

        Assert.Equal("ok", result);
        Assert.Equal(1, handler.SendCount);
    }
}

