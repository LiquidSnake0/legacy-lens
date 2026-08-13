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
        // nomic-embed-text emits 768 dimensions.
        var a = Enumerable.Range(0, 768).Select(i => (float)(i % 7)).ToArray();
        var b = Enumerable.Range(0, 768).Select(i => (float)(i % 7)).ToArray();
        Assert.Equal(1f, VectorMath.CosineSimilarity(a, b), 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(255)]
    [InlineData(769)]
    public void Handles_lengths_that_are_not_a_multiple_of_the_SIMD_width(int length)
    {
        // 768 divides evenly by every plausible Vector<float>.Count, so it
        // cannot catch a SIMD implementation that drops the leftover tail.
        // These lengths can.
        var a = Enumerable.Range(0, length).Select(i => (float)(i % 5 + 1)).ToArray();
        var b = Enumerable.Range(0, length).Select(i => (float)(i % 5 + 1)).ToArray();
        Assert.Equal(1f, VectorMath.CosineSimilarity(a, b), 3);

        // And a case where the tail is what makes the two differ.
        var c = (float[])a.Clone();
        c[^1] = -c[^1] * 100f;
        Assert.True(VectorMath.CosineSimilarity(a, c) < 0.999f,
            "A difference in the final element must change the score.");
    }
}
