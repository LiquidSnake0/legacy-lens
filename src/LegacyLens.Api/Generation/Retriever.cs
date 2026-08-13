using LegacyLens.Api.Embeddings;
using LegacyLens.Api.Storage;

namespace LegacyLens.Api.Generation;

/// <summary>
/// Turns a question into the set of chunks the model is allowed to see.
/// </summary>
public class Retriever
{
    private readonly IEmbeddingClient _embeddings;
    private readonly IVectorStore _store;

    /// <summary>
    /// Below this, a "match" is noise. Returning nothing is a better answer
    /// than six irrelevant excerpts and an invitation to improvise.
    /// </summary>
    public const float MinimumScore = 0.4f;

    /// <summary>Stops one verbose file from crowding out the one that holds the answer.</summary>
    public const int MaxChunksPerFile = 3;

    public Retriever(IEmbeddingClient embeddings, IVectorStore store)
    {
        _embeddings = embeddings;
        _store = store;
    }

    public async Task<IReadOnlyList<SearchHit>> RetrieveAsync(
        string question, int topK, CancellationToken ct = default)
    {
        var queryVector = await _embeddings.EmbedAsync(question, ct);

        // Over-fetch, because the per-file cap and the score floor will both
        // discard candidates and topK still has to be reachable afterwards.
        var candidates = await _store.SearchAsync(queryVector, topK * 4, ct);

        return Rank(candidates, topK);
    }

    /// <summary>
    /// Pure, so it can be tested without a model or a database.
    /// </summary>
    public static IReadOnlyList<SearchHit> Rank(IReadOnlyList<SearchHit> candidates, int topK)
    {
        // ---------------------------------------------------------------
        // TODO — see docs/TODO.md #4.
        //
        // Tests in tests/LegacyLens.Tests/RetrieverTests.cs cover the score
        // floor, the per-file cap, ordering, and topK.
        // ---------------------------------------------------------------
        throw new NotImplementedException(
            "Retriever.Rank is not implemented yet — see docs/TODO.md #4.");
    }
}
