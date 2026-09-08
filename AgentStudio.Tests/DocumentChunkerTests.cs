using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

public class DocumentChunkerTests
{
    [Fact]
    public void Empty_text_produces_no_chunks()
    {
        Assert.Empty(DocumentChunker.Chunk(""));
        Assert.Empty(DocumentChunker.Chunk("   "));
    }

    [Fact]
    public void Text_shorter_than_chunk_size_is_a_single_chunk()
    {
        var chunks = DocumentChunker.Chunk("hello world", chunkSize: 1000, overlap: 100);
        Assert.Single(chunks);
        Assert.Equal("hello world", chunks[0]);
    }

    [Fact]
    public void Long_text_is_split_with_overlap()
    {
        var text = new string('a', 250);
        var chunks = DocumentChunker.Chunk(text, chunkSize: 100, overlap: 20);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
            Assert.True(chunk.Length <= 100);

        // Reassembling with the known step size must reproduce the original text.
        var rebuilt = chunks[0] + string.Concat(chunks.Skip(1).Select(c => c[20..]));
        Assert.Equal(text, rebuilt);
    }

    [Fact]
    public void Last_chunk_is_not_padded()
    {
        var text = new string('a', 105);
        var chunks = DocumentChunker.Chunk(text, chunkSize: 100, overlap: 0);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(100, chunks[0].Length);
        Assert.Equal(5, chunks[1].Length);
    }
}
