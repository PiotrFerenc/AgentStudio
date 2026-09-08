using System.ClientModel;
using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentStudio.Infrastructure;

public sealed class OpenAiCompatibleEmbeddingClientFactory : IEmbeddingClientFactory
{
    public Application.IEmbeddingClient Create(ModelProviderConfig provider, string modelName)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl.TrimEnd('/') + "/v1"),
            NetworkTimeout = TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 1, 600))
        };
        var credential = new ApiKeyCredential(provider.ApiKey ?? "not-needed");
        var client = new OpenAIClient(credential, options);
        var generator = client.GetEmbeddingClient(modelName).AsIEmbeddingGenerator();
        return new MeiEmbeddingClientAdapter(generator);
    }

    private sealed class MeiEmbeddingClientAdapter(IEmbeddingGenerator<string, Embedding<float>> inner) : Application.IEmbeddingClient
    {
        public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var result = await inner.GenerateAsync([text], cancellationToken: ct);
            return result[0].Vector.ToArray();
        }
    }
}
