using LegacyLens.Api.Storage;

namespace LegacyLens.Tests;

public class VectorMathTests
{
    [Fact]
    public void Identical_vectors_score_one()
    {
        float[] v = [1f, 2f, 3f];
        Assert.Equal(1f, VectorMath.CosineSimilarity(v, v), 4);
    }

    [Fact]
    public void Orthogonal_vectors_score_zero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity([1f, 0f], [0f, 1f]), 4);
    }

    [Fact]
    public void Opposite_vectors_score_minus_one()
    {
        Assert.Equal(-1f, VectorMath.CosineSimilarity([1f, 2f], [-1f, -2f]), 4);
    }

    [Fact]
    public void Magnitude_does_not_change_the_score()
    {
        // The whole reason cosine is used rather than a dot product: a long
        // chunk must not outrank a short one just for being long.
        var short_ = VectorMath.CosineSimilarity([1f, 1f], [1f, 0f]);
        var long_  = VectorMath.CosineSimilarity([50f, 50f], [1f, 0f]);
        Assert.Equal(short_, long_, 4);
    }

    [Fact]
    public void Mismatched_lengths_throw()
    {
        Assert.Throws<ArgumentException>(() =>
            VectorMath.CosineSimilarity([1f, 2f, 3f], [1f, 2f]));
    }

    [Fact]
    public void Zero_vector_scores_zero_rather_than_NaN()
    {
        // A zero vector has no direction, so it has no angle to anything.
        // The maths says divide by zero; the caller needs a number it can sort.
        var score = VectorMath.CosineSimilarity([0f, 0f], [1f, 1f]);
        Assert.False(float.IsNaN(score));
        Assert.Equal(0f, score, 4);
    }

    [Fact]
    public void Works_at_embedding_model_dimensions()
    {
        // nomic-embed-text emits 768 dimensions. If a SIMD implementation
        // mishandles the tail beyond the last full vector width, this catches it.
        var a = Enumerable.Range(0, 768).Select(i => (float)(i % 7)).ToArray();
        var b = Enumerable.Range(0, 768).Select(i => (float)(i % 7)).ToArray();
        Assert.Equal(1f, VectorMath.CosineSimilarity(a, b), 3);
    }
}
