using LegacyLens.Api;
using LegacyLens.Api.Storage;
using Microsoft.Extensions.Configuration;

namespace LegacyLens.Tests;

/// <summary>
/// Exercises the store against a real SQLite file. The full-text half in
/// particular cannot be verified any other way: FTS5 either ships with the
/// SQLite build in use or it does not, and only running it says which.
/// </summary>
public class SqliteVectorStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"lens-store-{Guid.NewGuid():N}.db");

    private SqliteVectorStore Store()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["INDEX_PATH"] = _path })
            .Build();
        return new SqliteVectorStore(config);
    }

    private static EmbeddedChunk Chunk(string id, string content, float[] vector, string path = "A.cs") =>
        new(new Chunk(id, path, 1, 20, content), vector);

    [Fact]
    public async Task Round_trips_a_chunk_through_the_vector_search()
    {
        using var store = Store();
        await store.UpsertAsync([Chunk("a", "public class Thing { }", [1f, 0f, 0f])]);

        var hits = await store.SearchAsync([1f, 0f, 0f], 5);

        Assert.Single(hits);
        Assert.Equal(1f, hits[0].Score, 3);
    }

    [Fact]
    public async Task Finds_an_exact_identifier_by_text()
    {
        // The case the vector search misses: a rare proper noun the embedding
        // model never saw in training.
        using var store = Store();
        await store.UpsertAsync([
            Chunk("a", "public decimal ComputePriceEngineRate() { }", [1f, 0f]),
            Chunk("b", "public void SomethingElse() { }", [0f, 1f], "B.cs"),
        ]);

        var hits = await store.SearchTextAsync("ComputePriceEngineRate", 5);

        Assert.Single(hits);
        Assert.Equal("a", hits[0].Chunk.Id);
    }

    [Fact]
    public async Task Ranks_text_matches_with_the_best_first()
    {
        using var store = Store();
        await store.UpsertAsync([
            Chunk("dense", "Overlap Overlap Overlap", [1f, 0f]),
            Chunk("sparse", "Overlap and a great deal of other unrelated words here", [0f, 1f], "B.cs"),
        ]);

        var hits = await store.SearchTextAsync("Overlap", 5);

        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].Score >= hits[1].Score, "results are not ordered by relevance");
    }

    [Fact]
    public async Task Punctuation_in_a_question_does_not_break_the_search()
    {
        // Quotes, parentheses, hyphens and asterisks are all query syntax in
        // FTS5. User text reaching MATCH unchanged would throw.
        using var store = Store();
        await store.UpsertAsync([Chunk("a", "public void Compute() { }", [1f, 0f])]);

        var hits = await store.SearchTextAsync("Where is Compute() -- the \"real\" one? *", 5);

        Assert.Single(hits);
    }

    [Fact]
    public async Task A_question_with_no_usable_terms_returns_nothing()
    {
        using var store = Store();
        await store.UpsertAsync([Chunk("a", "content", [1f, 0f])]);

        Assert.Empty(await store.SearchTextAsync("? * ( )", 5));
    }

    [Fact]
    public async Task Re_indexing_does_not_duplicate_text_rows()
    {
        // FTS5 has no ON CONFLICT, so an upsert has to delete before inserting.
        using var store = Store();
        var chunk = Chunk("a", "UniqueMarker here", [1f, 0f]);

        await store.UpsertAsync([chunk]);
        await store.UpsertAsync([chunk]);

        Assert.Single(await store.SearchTextAsync("UniqueMarker", 10));
    }

    [Fact]
    public async Task Clearing_empties_both_indexes()
    {
        using var store = Store();
        await store.UpsertAsync([Chunk("a", "Marker", [1f, 0f])]);
        await store.ClearAsync();

        Assert.Empty(await store.SearchTextAsync("Marker", 5));
        Assert.Equal(0, await store.CountAsync());
    }

    [Fact]
    public async Task An_index_written_before_the_text_table_existed_is_backfilled()
    {
        // Opening an older index must not leave lexical search silently empty,
        // and re-embedding every chunk to fix it would cost minutes.
        using (var store = Store())
        {
            await store.UpsertAsync([Chunk("a", "BackfillMarker", [1f, 0f])]);
        }

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE chunks_fts";
            drop.ExecuteNonQuery();
        }

        using var reopened = Store();
        Assert.Single(await reopened.SearchTextAsync("BackfillMarker", 5));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
