namespace LegacyLens.Api.Generation;

/// <summary>A chunk with the rank it earned in each search.</summary>
public record FusedHit(
    Chunk Chunk,
    /// <summary>Cosine similarity, or null when only the text search found it.</summary>
    float? VectorScore,
    int? VectorRank,
    int? TextRank,
    double FusedScore);

/// <summary>
/// Merges the vector and full-text rankings.
///
/// The two scores cannot be compared or averaged: cosine similarity sits
/// between -1 and 1, BM25 is unbounded and depends on corpus statistics.
/// Normalising them would require assumptions about both distributions that
/// change with every codebase.
///
/// Reciprocal rank fusion sidesteps that by using only positions. A document
/// ranked third contributes 1/(k+3) regardless of which search produced it and
/// what number that search attached. Documents found by both rise; documents
/// found by one alone still appear.
/// </summary>
public static class HybridFusion
{
    /// <summary>
    /// The constant in 1/(k + rank). At 60, the difference between rank 1 and
    /// rank 2 stays meaningful while a document ranked 50th is not erased
    /// entirely. It is the value the original RRF paper used, and it is here as
    /// a named constant so that disagreeing with it is a one-line change.
    /// </summary>
    public const int RankConstant = 60;

    public static IReadOnlyList<FusedHit> Fuse(
        IReadOnlyList<SearchHit> vector,
        IReadOnlyList<SearchHit> text)
    {
        var byId = new Dictionary<string, FusedHit>(StringComparer.Ordinal);

        for (var i = 0; i < vector.Count; i++)
        {
            var hit = vector[i];
            byId[hit.Chunk.Id] = new FusedHit(
                hit.Chunk, hit.Score, i + 1, null, Contribution(i + 1));
        }

        for (var i = 0; i < text.Count; i++)
        {
            var hit = text[i];
            var rank = i + 1;

            if (byId.TryGetValue(hit.Chunk.Id, out var existing))
            {
                byId[hit.Chunk.Id] = existing with
                {
                    TextRank = rank,
                    FusedScore = existing.FusedScore + Contribution(rank),
                };
            }
            else
            {
                // Found by text alone. This is the case the whole feature
                // exists for: an exact identifier the embedding did not favour.
                byId[hit.Chunk.Id] = new FusedHit(
                    hit.Chunk, null, null, rank, Contribution(rank));
            }
        }

        return byId.Values.OrderByDescending(h => h.FusedScore).ToList();
    }

    private static double Contribution(int rank) => 1.0 / (RankConstant + rank);
}
