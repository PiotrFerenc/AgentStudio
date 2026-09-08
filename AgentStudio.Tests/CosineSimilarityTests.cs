using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

public class CosineSimilarityTests
{
    [Fact]
    public void Identical_vectors_score_one()
    {
        var v = new float[] { 1, 2, 3 };
        Assert.Equal(1.0, CosineSimilarity.Compute(v, v), precision: 6);
    }

    [Fact]
    public void Orthogonal_vectors_score_zero()
    {
        Assert.Equal(0.0, CosineSimilarity.Compute(new float[] { 1, 0 }, new float[] { 0, 1 }), precision: 6);
    }

    [Fact]
    public void Opposite_vectors_score_minus_one()
    {
        Assert.Equal(-1.0, CosineSimilarity.Compute(new float[] { 1, 0 }, new float[] { -1, 0 }), precision: 6);
    }

    [Fact]
    public void Mismatched_length_or_empty_scores_zero_instead_of_throwing()
    {
        Assert.Equal(0.0, CosineSimilarity.Compute(new float[] { 1, 2 }, new float[] { 1, 2, 3 }));
        Assert.Equal(0.0, CosineSimilarity.Compute(Array.Empty<float>(), Array.Empty<float>()));
    }

    [Fact]
    public void More_similar_vector_ranks_higher()
    {
        var query = new float[] { 1, 1, 0 };
        var close = new float[] { 1, 0.9f, 0 };
        var far = new float[] { 0, 0, 1 };

        Assert.True(CosineSimilarity.Compute(query, close) > CosineSimilarity.Compute(query, far));
    }
}
