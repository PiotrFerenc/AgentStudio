namespace AgentStudio.Domain;

/// <summary>Naive fixed-size character-window chunking with overlap (phase 2, RAG).
/// No sentence/token awareness — the smallest thing that lets long documents be searched
/// in pieces. Upgrade to a token-aware splitter if chunk boundaries measurably hurt search
/// quality.</summary>
public static class DocumentChunker
{
    public static List<string> Chunk(string text, int chunkSize = 1000, int overlap = 100)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        var step = Math.Max(1, chunkSize - overlap);
        for (var start = 0; start < text.Length; start += step)
        {
            var length = Math.Min(chunkSize, text.Length - start);
            chunks.Add(text.Substring(start, length));
            if (start + length >= text.Length) break;
        }
        return chunks;
    }
}
