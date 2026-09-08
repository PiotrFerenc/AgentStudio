using AgentStudio.Application;
using AgentStudio.Domain;

namespace AgentStudio.Infrastructure;

public sealed class DocumentSearchService : IDocumentSearchService
{
    private readonly IDocumentRepository _documents;
    private readonly IEmbeddingClientFactory _embeddings;

    public DocumentSearchService(IDocumentRepository documents, IEmbeddingClientFactory embeddings)
    {
        _documents = documents;
        _embeddings = embeddings;
    }

    public async Task<List<string>> SearchAsync(Guid agentId, string query, int topK, ModelProviderConfig provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.EmbeddingModel))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no embedding model configured — set one on the Providers page.");

        var chunks = await _documents.ListChunksAsync(agentId, ct);
        if (chunks.Count == 0) return new List<string>();

        var client = _embeddings.Create(provider, provider.EmbeddingModel);
        var queryEmbedding = await client.EmbedAsync(query, ct);

        // ponytail: brute-force cosine scan over every chunk this agent has — fine at
        // "documents per agent" scale (this is what a real DB round-trip would cost anyway for
        // small corpora, and it needs zero extra infrastructure). Move to pgvector or another
        // ANN index if a single agent's corpus grows past roughly 100k chunks and this becomes
        // measurably slow — see PLAN.md faza 2 etap 5 for why pgvector wasn't the default.
        return chunks
            .Select(c => (Chunk: c, Score: CosineSimilarity.Compute(queryEmbedding, c.Embedding)))
            .OrderByDescending(x => x.Score)
            .Take(Math.Max(1, topK))
            .Select(x => x.Chunk.Text)
            .ToList();
    }
}
