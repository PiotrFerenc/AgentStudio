using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public class DocumentIndexerTests
{
    private sealed class FakeEmbeddingClient : Application.IEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new float[] { 1, 0, 0 });
    }

    private sealed class FakeEmbeddingClientFactory : IEmbeddingClientFactory
    {
        public Application.IEmbeddingClient Create(ModelProviderConfig provider, string modelName) => new FakeEmbeddingClient();
    }

    private static (DocumentIndexer Indexer, IDocumentRepository Repo) NewIndexer(string dbName, string storageDir)
    {
        var db = new AgentStudioDbContext(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options);
        var repo = new DocumentRepository(db);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Documents:StoragePath"] = storageDir })
            .Build();
        return (new DocumentIndexer(repo, new FakeEmbeddingClientFactory(), config), repo);
    }

    [Fact]
    public async Task IndexAsync_without_embedding_model_throws()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (indexer, _) = NewIndexer("idx-no-model", dir);
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://x" }; // no EmbeddingModel
        using var content = new MemoryStream("hello"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            indexer.IndexAsync(Guid.NewGuid(), "notes.txt", content, provider));
    }

    [Fact]
    public async Task IndexAsync_unsupported_extension_throws()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (indexer, _) = NewIndexer("idx-bad-ext", dir);
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://x", EmbeddingModel = "embed" };
        using var content = new MemoryStream("not really a pdf"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            indexer.IndexAsync(Guid.NewGuid(), "document.pdf", content, provider));
    }

    [Fact]
    public async Task IndexAsync_chunks_embeds_and_stores_a_plain_text_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (indexer, repo) = NewIndexer("idx-happy-path", dir);
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://x", EmbeddingModel = "embed" };
        var agentId = Guid.NewGuid();
        using var content = new MemoryStream("hello world, this is a small test document."u8.ToArray());

        var document = await indexer.IndexAsync(agentId, "notes.txt", content, provider);

        Assert.True(File.Exists(document.StoragePath));
        var chunks = await repo.ListChunksAsync(agentId);
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal(new List<float> { 1, 0, 0 }, c.Embedding));

        var listed = await indexer.ListAsync(agentId);
        Assert.Single(listed);
    }

    [Fact]
    public async Task DeleteAsync_removes_document_chunks_and_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (indexer, repo) = NewIndexer("idx-delete", dir);
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://x", EmbeddingModel = "embed" };
        var agentId = Guid.NewGuid();
        using var content = new MemoryStream("some content to delete later"u8.ToArray());
        var document = await indexer.IndexAsync(agentId, "gone.txt", content, provider);

        await indexer.DeleteAsync(document.Id);

        Assert.False(File.Exists(document.StoragePath));
        Assert.Empty(await repo.ListChunksAsync(agentId));
        Assert.Empty(await indexer.ListAsync(agentId));
    }
}
