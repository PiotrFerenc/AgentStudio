using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentStudio.Tests;

public class DocumentSearchServiceTests
{
    /// <summary>Deterministic fake: embeds a string as a one-hot vector over a fixed vocabulary,
    /// so ranking is fully predictable without a real embedding endpoint.</summary>
    private sealed class FakeEmbeddingClient : Application.IEmbeddingClient
    {
        private static readonly string[] Vocabulary = { "cats", "dogs", "postgres", "unrelated" };

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var vector = new float[Vocabulary.Length];
            for (var i = 0; i < Vocabulary.Length; i++)
                if (text.Contains(Vocabulary[i], StringComparison.OrdinalIgnoreCase))
                    vector[i] = 1;
            return Task.FromResult(vector);
        }
    }

    private sealed class FakeEmbeddingClientFactory : IEmbeddingClientFactory
    {
        public Application.IEmbeddingClient Create(ModelProviderConfig provider, string modelName) => new FakeEmbeddingClient();
    }

    private static AgentStudioDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(name).Options);

    [Fact]
    public async Task Returns_top_k_chunks_ranked_by_similarity()
    {
        using var db = NewDb("docsearch-rank");
        var repo = new DocumentRepository(db);
        var agentId = Guid.NewGuid();

        var about = new Func<string, float[]> (word =>
        {
            var v = new float[4];
            v[Array.IndexOf(new[] { "cats", "dogs", "postgres", "unrelated" }, word)] = 1;
            return v;
        });

        await repo.AddAsync(new Document { AgentId = agentId, FileName = "a.txt", StoragePath = "a" });
        await repo.AddChunksAsync(new[]
        {
            new DocumentChunk { AgentId = agentId, DocumentId = Guid.NewGuid(), ChunkIndex = 0, Text = "all about cats", Embedding = about("cats").ToList() },
            new DocumentChunk { AgentId = agentId, DocumentId = Guid.NewGuid(), ChunkIndex = 0, Text = "all about dogs", Embedding = about("dogs").ToList() },
            new DocumentChunk { AgentId = agentId, DocumentId = Guid.NewGuid(), ChunkIndex = 0, Text = "postgres tuning tips", Embedding = about("postgres").ToList() },
            new DocumentChunk { AgentId = agentId, DocumentId = Guid.NewGuid(), ChunkIndex = 0, Text = "totally unrelated text", Embedding = about("unrelated").ToList() },
        });
        await repo.SaveChangesAsync();

        var service = new DocumentSearchService(repo, new FakeEmbeddingClientFactory());
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://x", EmbeddingModel = "embed" };

        var results = await service.SearchAsync(agentId, "tell me about cats", topK: 2, provider);

        Assert.Equal(2, results.Count);
        Assert.Equal("all about cats", results[0]); // exact vocabulary match ranks first
    }

    [Fact]
    public async Task No_documents_returns_empty_list()
    {
        using var db = NewDb("docsearch-empty");
        var repo = new DocumentRepository(db);
        var service = new DocumentSearchService(repo, new FakeEmbeddingClientFactory());
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://x", EmbeddingModel = "embed" };

        var results = await service.SearchAsync(Guid.NewGuid(), "anything", topK: 3, provider);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Missing_embedding_model_throws_clear_error()
    {
        using var db = NewDb("docsearch-no-model");
        var repo = new DocumentRepository(db);
        var service = new DocumentSearchService(repo, new FakeEmbeddingClientFactory());
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://x" }; // no EmbeddingModel

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(Guid.NewGuid(), "q", 3, provider));
    }
}
