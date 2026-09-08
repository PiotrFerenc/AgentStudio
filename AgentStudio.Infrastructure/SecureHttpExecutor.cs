using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Infrastructure;

/// <summary>
/// Executes HTTP tool calls with SSRF protection: blocks loopback, link-local, private ranges
/// unless the host is present in the configured allowlist. Limits response size and redirects.
/// </summary>
public sealed class SecureHttpExecutor : ISecureHttpExecutor
{
    private static readonly string[] AllowedMethods = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
    private static readonly string[] ForbiddenHeaders = { "authorization", "cookie", "x-api-key", "proxy-authorization" };
    private const int MaxResponseBytes = 1_000_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SecureHttpExecutor> _logger;
    private readonly HashSet<string> _hostAllowlist;

    public SecureHttpExecutor(IHttpClientFactory httpClientFactory, ILogger<SecureHttpExecutor> logger, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _hostAllowlist = new HashSet<string>(
            (config.GetSection("HttpTool:AllowedHosts").GetChildren().Select(c => c.Value).Where(v => v is not null).Cast<string>())
            .Select(h => h.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string> ExecuteAsync(HttpNode node, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default)
    {
        var method = node.Method.ToUpperInvariant();
        if (!AllowedMethods.Contains(method))
            throw new InvalidOperationException($"HTTP method {method} is not allowed.");

        if (!Uri.TryCreate(node.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"Invalid or non-HTTP URL: {node.Url}");

        await ValidateHostAsync(uri, ct);

        var attempts = Math.Max(0, node.Retries) + 1;
        Exception? lastError = null;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                return await SendOnceAsync(uri, node, method, ct);
            }
            catch (Exception ex) when (i < attempts - 1)
            {
                lastError = ex;
                _logger.LogWarning("HTTP tool attempt {Attempt} failed: {Error}", i + 1, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (i + 1)), ct);
            }
        }
        throw lastError ?? new InvalidOperationException("HTTP request failed.");
    }

    private async Task<string> SendOnceAsync(Uri uri, HttpNode node, string method, CancellationToken ct)
    {
        var builder = new UriBuilder(uri);
        if (node.QueryParameters.Count > 0)
        {
            var query = new StringBuilder(builder.Query.TrimStart('?'));
            foreach (var (key, value) in node.QueryParameters)
            {
                if (query.Length > 0) query.Append('&');
                query.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            }
            builder.Query = query.ToString();
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), builder.Uri);
        foreach (var (key, value) in node.Headers)
        {
            if (ForbiddenHeaders.Contains(key.ToLowerInvariant()))
            {
                _logger.LogWarning("HTTP tool: header {Header} was dropped for safety.", key);
                continue;
            }
            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (node.Body is not null && method is not "GET" and not "HEAD")
            request.Content = new StringContent(node.Body, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient("agentstudio-http-tool");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(node.TimeoutSeconds, 1, 300));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await ReadLimitedAsync(response, ct);
        var body = Encoding.UTF8.GetString(bytes);
        return body;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxResponseBytes)
                throw new InvalidOperationException($"HTTP response exceeded the {MaxResponseBytes}-byte limit.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private async Task ValidateHostAsync(Uri uri, CancellationToken ct)
    {
        var host = uri.Host.ToLowerInvariant();
        if (_hostAllowlist.Contains(host)) return;

        if (host is "localhost" or "127.0.0.1" or "::1")
            throw new InvalidOperationException("HTTP tool: loopback addresses are blocked.");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"HTTP tool: cannot resolve host {host}: {ex.Message}");
        }

        foreach (var ip in addresses)
        {
            if (IPAddress.IsLoopback(ip))
                throw new InvalidOperationException("HTTP tool: loopback addresses are blocked.");

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                var blocked =
                    b[0] == 10 ||
                    (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                    (b[0] == 192 && b[1] == 168) ||
                    (b[0] == 169 && b[1] == 254) ||
                    b[0] == 0 || b[0] == 127;
                if (blocked)
                    throw new InvalidOperationException($"HTTP tool: private or reserved address {ip} is blocked. Add the host to HttpTool:AllowedHosts to allow internal calls.");
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
                    throw new InvalidOperationException($"HTTP tool: private IPv6 address {ip} is blocked.");
            }
        }
    }
}
