using System.ClientModel;
using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentStudio.Infrastructure;

public sealed class OpenAiCompatibleChatClientFactory : IChatClientFactory
{
    public Application.IChatClient Create(ModelProviderConfig provider, string modelName)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl.TrimEnd('/') + "/v1"),
            // The SDK's own default (100s) is lower than ModelProviderConfig.TimeoutSeconds'
            // own default (120s) and cuts off slow-but-legitimate completions with no way to
            // configure around it — wire the provider's own setting through instead of leaving
            // it unused.
            NetworkTimeout = TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 1, 600))
        };
        var credential = new ApiKeyCredential(provider.ApiKey ?? "not-needed");
        var client = new OpenAIClient(credential, options);
        var chatClient = client.GetChatClient(modelName).AsIChatClient();
        return new MeiChatClientAdapter(chatClient);
    }

    private sealed class MeiChatClientAdapter(Microsoft.Extensions.AI.IChatClient inner) : Application.IChatClient
    {
        public async IAsyncEnumerable<string> StreamReplyAsync(
            IReadOnlyList<Domain.ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var meiMessages = messages.Select(m => new Microsoft.Extensions.AI.ChatMessage(
                m.Role switch
                {
                    "system" => Microsoft.Extensions.AI.ChatRole.System,
                    "assistant" => Microsoft.Extensions.AI.ChatRole.Assistant,
                    _ => Microsoft.Extensions.AI.ChatRole.User
                }, m.Content)).ToList();

            await foreach (var update in inner.GetStreamingResponseAsync(meiMessages, cancellationToken: ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    yield return update.Text;
            }
        }
    }
}
