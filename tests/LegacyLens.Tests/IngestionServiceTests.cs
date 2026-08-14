using LegacyLens.Api.Embeddings;
using LegacyLens.Api.Ingestion;
using LegacyLens.Api.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LegacyLens.Tests;

public class IngestionServiceTests : IDisposable
{
    private readonly string _db = Path.Combine(
        Path.GetTempPath(), $"lens-ingest-{Guid.NewGuid():N}.db");

    private readonly string _repo = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"lens-repo-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// Counts the files it was asked to embed, and can be told to fail on one
    /// of them. Embedding is called once per file, so the call count is the
    /// number of files this run paid for.
    /// </summary>
    private class CountingEmbeddings : IEmbeddingClient
    {
        private readonly int _failOnCall;
        public int Calls { get; private set; }

        public CountingEmbeddings(int failOnCall = 0) => _failOnCall = failOnCall;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new[] { 1f, 0f });

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            Calls++;
            if (Calls == _failOnCall)
                throw new HttpRequestException("the embedding backend went away");

            return Task.FromResult<IReadOnlyList<float[]>>(
                texts.Select(_ => new[] { 1f, 0f }).ToList());
        }
    }

    private SqliteVectorStore Store()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["INDEX_PATH"] = _db })
            .Build();
        return new SqliteVectorStore(config);
    }

    private static IngestionService Service(IVectorStore store, IEmbeddingClient embeddings) =>
        new(new SourceWalker(), new CodeChunker(), embeddings, store,
            NullLogger<IngestionService>.Instance);

    private void WriteSource(string name, string marker)
    {
        File.WriteAllText(Path.Combine(_repo, name), $$"""
            namespace Sample;

            public class {{Path.GetFileNameWithoutExtension(name)}}
            {
                public string Describe() => "{{marker}}";
            }
            """);
    }

    [Fact]
    public async Task A_crash_part_way_through_keeps_the_files_it_had_already_embedded()
    {
        // The whole point of resuming. A two-hour run that dies near the end
        // used to lose every vector, because nothing was written until the last
        // statement of the method.
        WriteSource("A.cs", "AlphaMarker");
        WriteSource("B.cs", "BravoMarker");
        WriteSource("C.cs", "CharlieMarker");

        using var store = Store();
        var embeddings = new CountingEmbeddings(failOnCall: 3);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Service(store, embeddings).IngestAsync(_repo));

        // Two files reached the store and the ledger; the third is the one the
        // crash cost. Which two depends on the order the walk returned them,
        // and the guarantee is about the count, not the names.
        Assert.Equal(2, new IngestionLedger(store.Connection).Known().Count);
    }

    [Fact]
    public async Task The_next_run_pays_only_for_what_the_crash_did_not_reach()
    {
        WriteSource("A.cs", "AlphaMarker");
        WriteSource("B.cs", "BravoMarker");
        WriteSource("C.cs", "CharlieMarker");

        using var store = Store();
        await Assert.ThrowsAsync<HttpRequestException>(
            () => Service(store, new CountingEmbeddings(failOnCall: 3)).IngestAsync(_repo));

        var resumed = new CountingEmbeddings();
        var response = await Service(store, resumed).IngestAsync(_repo);

        // One file left to embed, not three: the ledger records a file only
        // once its chunks are stored, so the two survivors are skipped and the
        // interrupted one is redone.
        Assert.Equal(1, resumed.Calls);
        Assert.Equal(3, new IngestionLedger(store.Connection).Known().Count);
        Assert.True(response.ChunksIndexed > 0);
    }

    [Fact]
    public async Task A_file_that_was_being_indexed_when_it_crashed_is_not_left_half_written()
    {
        // The dangerous direction is the other one: marking a file done before
        // its chunks are stored leaves a hole nothing ever fills, because the
        // next run sees a hash that matches and skips it forever.
        WriteSource("A.cs", "AlphaMarker");
        WriteSource("B.cs", "BravoMarker");

        using var store = Store();
        await Assert.ThrowsAsync<HttpRequestException>(
            () => Service(store, new CountingEmbeddings(failOnCall: 1)).IngestAsync(_repo));

        Assert.Empty(new IngestionLedger(store.Connection).Known());

        await Service(store, new CountingEmbeddings()).IngestAsync(_repo);

        Assert.Single(await store.SearchTextAsync("AlphaMarker", 5));
        Assert.Single(await store.SearchTextAsync("BravoMarker", 5));
    }

    [Fact]
    public async Task A_complete_run_leaves_nothing_for_the_next_one_to_do()
    {
        WriteSource("A.cs", "AlphaMarker");
        WriteSource("B.cs", "BravoMarker");

        using var store = Store();
        await Service(store, new CountingEmbeddings()).IngestAsync(_repo);

        var second = new CountingEmbeddings();
        var response = await Service(store, second).IngestAsync(_repo);

        Assert.Equal(0, second.Calls);
        Assert.Equal(0, response.ChunksIndexed);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_db)) File.Delete(_db);
        if (Directory.Exists(_repo)) Directory.Delete(_repo, true);
    }
}
