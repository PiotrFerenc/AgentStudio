using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Infrastructure;

public sealed class DocumentIndexer : IDocumentIndexer
{
    // Phase 2 scope: plain-text uploads only. Binary formats (PDF, docx, ...) would need a
    // dedicated parser — a bigger dependency question left for a later phase.
    private static readonly string[] AllowedExtensions = { ".txt", ".md", ".csv", ".json", ".log" };

    private readonly IDocumentRepository _documents;
    private readonly IEmbeddingClientFactory _embeddings;
    private readonly string _storageRoot;

    public DocumentIndexer(IDocumentRepository documents, IEmbeddingClientFactory embeddings, IConfiguration config)
    {
        _documents = documents;
        _embeddings = embeddings;
        _storageRoot = config["Documents:StoragePath"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "App_Data", "documents");
    }

    public async Task<Document> IndexAsync(Guid agentId, string fileName, Stream content, ModelProviderConfig provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.EmbeddingModel))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no embedding model configured — set one on the Providers page.");

        var extension = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported file type '{extension}'. Allowed: {string.Join(", ", AllowedExtensions)} (plain text only).");

        var document = new Document { AgentId = agentId, FileName = fileName, StoragePath = "" };

        var agentDir = Path.Combine(_storageRoot, agentId.ToString());
        Directory.CreateDirectory(agentDir);
        var path = Path.Combine(agentDir, $"{document.Id}{extension}");
        document.StoragePath = path;

        await using (var fileStream = File.Create(path))
            await content.CopyToAsync(fileStream, ct);

        var text = await File.ReadAllTextAsync(path, ct);
        var chunks = DocumentChunker.Chunk(text);

        var client = _embeddings.Create(provider, provider.EmbeddingModel);
        var docChunks = new List<DocumentChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var embedding = await client.EmbedAsync(chunks[i], ct);
            docChunks.Add(new DocumentChunk { DocumentId = document.Id, AgentId = agentId, ChunkIndex = i, Text = chunks[i], Embedding = embedding.ToList() });
        }

        await _documents.AddAsync(document, ct);
        await _documents.AddChunksAsync(docChunks, ct);
        await _documents.SaveChangesAsync(ct);
        return document;
    }

    public Task<List<Document>> ListAsync(Guid agentId, CancellationToken ct = default) => _documents.ListAsync(agentId, ct);

    public async Task DeleteAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await _documents.GetAsync(documentId, ct) ?? throw new KeyNotFoundException("Document not found.");
        await _documents.DeleteChunksAsync(documentId, ct);
        await _documents.DeleteAsync(document, ct);
        await _documents.SaveChangesAsync(ct);

        try { if (File.Exists(document.StoragePath)) File.Delete(document.StoragePath); }
        catch { /* best-effort cleanup — the DB rows are already gone, a stray file isn't fatal */ }
    }
}
