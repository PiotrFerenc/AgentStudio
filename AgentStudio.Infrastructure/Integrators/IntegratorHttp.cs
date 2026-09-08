using System.Text;

namespace AgentStudio.Infrastructure.Integrators;

/// <summary>Shared HTTP send/read/error-check helper for IIntegrator implementations. Caps the
/// response body like SecureHttpExecutor does for HttpNode — a misbehaving or compromised
/// integration endpoint (or a host reached via the shared client's redirects) otherwise has no
/// limit on how much it can make an integrator buffer into memory. New integrators should call
/// this instead of hand-rolling client.SendAsync/ReadAsStringAsync.</summary>
internal static class IntegratorHttp
{
    private const int MaxResponseBytes = 1_000_000;

    public static async Task<string> SendAsync(HttpClient client, HttpRequestMessage request, string integratorName, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await ReadLimitedAsync(response, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{integratorName} failed ({(int)response.StatusCode}): {body}");
        return body;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxResponseBytes)
                throw new InvalidOperationException($"Integrator response exceeded the {MaxResponseBytes}-byte limit.");
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
