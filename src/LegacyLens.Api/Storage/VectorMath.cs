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
        if (a.Length != b.Length)
            throw new ArgumentException(
                $"Vectors must have the same number of dimensions: got {a.Length} and {b.Length}. " +
                "In practice this means the index was built with a different embedding model " +
                "than the one answering queries, reindex rather than compare across spaces.",
                nameof(b));

        // One pass, three accumulators. In double, because summing hundreds of
        // squared floats accumulates enough rounding error to make a vector
        // score 0.9998 against itself.
        double dot = 0, squaredA = 0, squaredB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            double x = a[i], y = b[i];
            dot      += x * y;
            squaredA += x * x;
            squaredB += y * y;
        }

        // A zero vector has no direction, so there is no angle to measure. The
        // arithmetic would say NaN, and NaN poisons everything downstream:
        // every comparison against it is false, so the ranking silently stops
        // being a ranking. Zero is a number that sorts.
        if (squaredA == 0 || squaredB == 0) return 0f;

        var cosine = dot / (Math.Sqrt(squaredA) * Math.Sqrt(squaredB));

        // Rounding can push an exact match a hair beyond 1.
        return (float)Math.Clamp(cosine, -1.0, 1.0);
    }
}
