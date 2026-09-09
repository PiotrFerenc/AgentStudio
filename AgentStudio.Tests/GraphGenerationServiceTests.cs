using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

public class GraphGenerationServiceTests
{
    private sealed class FakeChatClient : Application.IChatClient
    {
        private readonly string _response;
        public FakeChatClient(string response) => _response = response;

        public async IAsyncEnumerable<string> StreamReplyAsync(
            IReadOnlyList<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return _response;
        }
    }

    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        private readonly string _response;
        public FakeChatClientFactory(string response) => _response = response;
        public Application.IChatClient Create(ModelProviderConfig provider, string modelName) => new FakeChatClient(_response);
    }

    private sealed class FakeProviderRepository : IProviderRepository
    {
        private readonly ModelProviderConfig? _provider;
        public FakeProviderRepository(ModelProviderConfig? provider) => _provider = provider;
        public Task<ModelProviderConfig?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult(_provider);
        public Task<List<ModelProviderConfig>> ListAsync(CancellationToken ct = default) => Task.FromResult(_provider is null ? new List<ModelProviderConfig>() : new List<ModelProviderConfig> { _provider });
    }

    private static (GraphGenerationService Service, Agent Agent) Fixture(string modelResponse)
    {
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://x" };
        var service = new GraphGenerationService(new FakeChatClientFactory(modelResponse), new FakeProviderRepository(provider));
        var agent = new Agent { Name = "t", ModelProviderName = "p", ModelName = "m" };
        return (service, agent);
    }

    private const string ValidGraphJson = """
        {"nodes":[
          {"id":"n1","type":"start","label":"","x":0,"y":0,"props":{}},
          {"id":"n2","type":"message","label":"","x":200,"y":0,"props":{"text":"hi"}},
          {"id":"n3","type":"end","label":"","x":400,"y":0,"props":{}}
        ],"edges":[
          {"id":"e1","sourceNodeId":"n1","targetNodeId":"n2","branch":null},
          {"id":"e2","sourceNodeId":"n2","targetNodeId":"n3","branch":null}
        ]}
        """;

    [Fact]
    public async Task GenerateGraphAsync_valid_json_returns_success()
    {
        var (service, agent) = Fixture(ValidGraphJson);

        var result = await service.GenerateGraphAsync(agent, "say hi");

        Assert.True(result.Success);
        Assert.Equal(3, result.Graph!.Nodes.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task GenerateGraphAsync_strips_markdown_code_fence()
    {
        var (service, agent) = Fixture($"```json\n{ValidGraphJson}\n```");

        var result = await service.GenerateGraphAsync(agent, "say hi");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task GenerateGraphAsync_unknown_node_type_fails_clearly()
    {
        var (service, agent) = Fixture("""{"nodes":[{"id":"n1","type":"not-a-real-type","label":"","x":0,"y":0,"props":{}}],"edges":[]}""");

        var result = await service.GenerateGraphAsync(agent, "do something weird");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("Unknown node type"));
    }

    [Fact]
    public async Task GenerateGraphAsync_missing_end_node_fails_validation()
    {
        var (service, agent) = Fixture("""{"nodes":[{"id":"n1","type":"start","label":"","x":0,"y":0,"props":{}}],"edges":[]}""");

        var result = await service.GenerateGraphAsync(agent, "just start");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("End node"));
    }

    [Fact]
    public async Task GenerateGraphAsync_not_json_fails_clearly()
    {
        var (service, agent) = Fixture("sure, here's a graph: <a graph>");

        var result = await service.GenerateGraphAsync(agent, "say hi");

        Assert.False(result.Success);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task GenerateGraphAsync_empty_instructions_fails_without_calling_the_model()
    {
        var (service, agent) = Fixture(ValidGraphJson);

        var result = await service.GenerateGraphAsync(agent, "  ");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task GenerateGraphAsync_missing_provider_fails_clearly()
    {
        var service = new GraphGenerationService(new FakeChatClientFactory(ValidGraphJson), new FakeProviderRepository(null));
        var agent = new Agent { Name = "t", ModelProviderName = "missing", ModelName = "m" };

        var result = await service.GenerateGraphAsync(agent, "say hi");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("missing"));
    }

    [Fact]
    public void BuildSystemPrompt_mentions_every_node_catalog_type()
    {
        var prompt = GraphGenerationService.BuildSystemPrompt();

        foreach (var type in NodeCatalog.Nodes.Keys)
            Assert.Contains($"\"{type}\"", prompt);
    }

    [Theory]
    [InlineData("plain json, no fence")]
    [InlineData("```\nfenced, no language tag\n```")]
    [InlineData("```json\nfenced with language tag\n```")]
    public void StripCodeFence_returns_inner_content_for_fenced_and_unfenced_text(string wrapper)
    {
        var inner = wrapper.Contains("no fence") ? wrapper : wrapper.Split('\n')[1];
        Assert.Equal(inner, GraphGenerationService.StripCodeFence(wrapper));
    }
}
