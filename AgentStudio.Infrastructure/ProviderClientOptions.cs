using System.ClientModel.Primitives;
using AgentStudio.Domain;
using OpenAI;

namespace AgentStudio.Infrastructure;

/// <summary>Builds the OpenAIClientOptions shared by the chat and embedding client factories —
/// timeout and custom-header wiring used to be duplicated in both.</summary>
public static class ProviderClientOptions
{
    public static OpenAIClientOptions Build(ModelProviderConfig provider)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl.TrimEnd('/') + "/v1"),
            // The SDK's own default (100s) is lower than ModelProviderConfig.TimeoutSeconds' own
            // default (120s) and cuts off slow-but-legitimate completions with no way to
            // configure around it — wire the provider's own setting through instead.
            NetworkTimeout = TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 1, 600))
        };
        if (provider.Headers.Count > 0)
            options.AddPolicy(new CustomHeadersPolicy(provider.Headers), PipelinePosition.PerCall);
        return options;
    }

    /// <summary>Sets each configured header on every outgoing request — e.g. a gateway auth
    /// header an OpenAI-compatible proxy expects alongside/instead of the bearer ApiKey.</summary>
    private sealed class CustomHeadersPolicy(IReadOnlyDictionary<string, string> headers) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            pipeline[currentIndex + 1].Process(message, pipeline, currentIndex + 1);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            return pipeline[currentIndex + 1].ProcessAsync(message, pipeline, currentIndex + 1);
        }

        private void Apply(PipelineMessage message)
        {
            foreach (var (key, value) in headers)
                message.Request.Headers.Set(key, value);
        }
    }
}
