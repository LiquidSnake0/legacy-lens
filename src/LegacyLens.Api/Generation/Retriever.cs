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

        // Over-fetch, because the score floor and the per-file cap both discard
        // candidates and topK still has to be reachable afterwards.
        //
        // Against the current store this multiplier buys nothing and costs
        // nothing: the scan already scores every chunk, so asking for more only
        // changes how many rows survive a Take. It exists for the day an
        // approximate index makes asking for more actually cost something.
        var candidates = await _store.SearchAsync(queryVector, topK * 4, ct);

        return Rank(candidates, topK);
    }

    /// <summary>
    /// Pure, so it can be tested without a model or a database.
    /// </summary>
    public static IReadOnlyList<SearchHit> Rank(IReadOnlyList<SearchHit> candidates, int topK)
    {
        // The order of these steps is load-bearing.
        //
        // Capping per file has to happen before the global cut, not after:
        // trimming to topK first and then discarding the surplus of a
        // dominant file leaves fewer results than asked for, and the file
        // that should have taken the freed slots is already gone.
        //
        // The final sort is a contract with PromptBuilder, which fills its
        // budget from the front and stops. An unordered list here means the
        // strongest evidence is what gets dropped there.
        return candidates
            .Where(hit => hit.Score >= MinimumScore)
            .GroupBy(hit => hit.Chunk.FilePath, StringComparer.Ordinal)
            .SelectMany(file => file
                .OrderByDescending(hit => hit.Score)
                .Take(MaxChunksPerFile))
            .OrderByDescending(hit => hit.Score)
            .Take(topK)
            .ToList();
    }
}
