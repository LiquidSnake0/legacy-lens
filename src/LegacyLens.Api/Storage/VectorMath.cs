namespace LegacyLens.Api.Storage;

public static class VectorMath
{
    /// <summary>
    /// Cosine similarity between two vectors: 1 means identical direction,
    /// 0 orthogonal, -1 opposite.
    /// </summary>
    /// <exception cref="ArgumentException">If the lengths differ.</exception>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        // ---------------------------------------------------------------
        // TODO — see docs/TODO.md #2.
        //
        // Tests in tests/LegacyLens.Tests/VectorMathTests.cs cover the
        // identical / orthogonal / opposite cases, mismatched lengths, and
        // the zero vector — which has no direction, so it has no cosine.
        // Decide what that returns and make it explicit.
        // ---------------------------------------------------------------
        throw new NotImplementedException(
            "VectorMath.CosineSimilarity is not implemented yet — see docs/TODO.md #2.");
    }
}
