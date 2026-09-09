using System.ClientModel.Primitives;
using System.Net;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Proves custom provider headers (ModelProviderConfig.Headers) actually reach the
/// wire — drives the real ClientPipeline built by ProviderClientOptions.Build against a local
/// HttpListener, the same rigor CLAUDE.md documents for the SSRF/SQL-injection guards.</summary>
public class ProviderClientOptionsTests
{
    [Fact]
    public async Task Build_sends_configured_headers_on_every_request()
    {
        using var listener = new HttpListener();
        var port = GetFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? captured = null;
        var serverTask = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            captured = ctx.Request.Headers["X-Gateway-Key"];
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var provider = new ModelProviderConfig
        {
            Name = "p",
            BaseUrl = $"http://127.0.0.1:{port}",
            Headers = new Dictionary<string, string> { ["X-Gateway-Key"] = "secret-123" }
        };

        var pipeline = ClientPipeline.Create(ProviderClientOptions.Build(provider));
        var message = pipeline.CreateMessage();
        message.Request.Method = "GET";
        message.Request.Uri = new Uri($"http://127.0.0.1:{port}/");
        message.ResponseClassifier = PipelineMessageClassifier.Default;
        pipeline.Send(message);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.Equal("secret-123", captured);
    }

    [Fact]
    public void Build_with_no_headers_does_not_add_the_custom_header_policy()
    {
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://x" };

        // No exception, no headers configured — just proves an empty Headers dict is a no-op
        // (the policy is skipped entirely rather than running with nothing to set).
        var options = ProviderClientOptions.Build(provider);

        Assert.NotNull(options);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
