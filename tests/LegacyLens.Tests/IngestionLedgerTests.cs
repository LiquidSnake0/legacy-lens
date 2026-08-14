using LegacyLens.Api;
using LegacyLens.Api.Ingestion;
using LegacyLens.Api.Storage;
using Microsoft.Extensions.Configuration;

namespace LegacyLens.Tests;

public class IngestionLedgerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"lens-ledger-{Guid.NewGuid():N}.db");

    private SqliteVectorStore Store()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["INDEX_PATH"] = _path })
            .Build();
        return new SqliteVectorStore(config);
    }

    [Fact]
    public void The_same_content_hashes_the_same_regardless_of_when_it_was_read()
    {
        // Files are keyed by content, not by timestamp: a checkout, a branch
        // switch or a restored backup all rewrite modification times without
        // changing a line, and would otherwise force a full re-index.
        Assert.Equal(IngestionLedger.Hash("class C { }"), IngestionLedger.Hash("class C { }"));
        Assert.NotEqual(IngestionLedger.Hash("class C { }"), IngestionLedger.Hash("class D { }"));
    }

    [Fact]
    public void Records_survive_to_the_next_run()
    {
        using var store = Store();
        var ledger = new IngestionLedger(store.Connection);

        ledger.Record("src/A.cs", "hash-a", 4);

        Assert.Equal("hash-a", new IngestionLedger(store.Connection).Known()["src/A.cs"]);
    }

    [Fact]
    public void Re_recording_a_file_replaces_rather_than_duplicates()
    {
        using var store = Store();
        var ledger = new IngestionLedger(store.Connection);

        ledger.Record("src/A.cs", "before", 4);
        ledger.Record("src/A.cs", "after", 6);

        var known = ledger.Known();
        Assert.Single(known);
        Assert.Equal("after", known["src/A.cs"]);
    }

    [Fact]
    public async Task Forgetting_a_file_removes_its_chunks_from_both_indexes()
    {
        // A deleted file whose chunks stay behind answers questions with code
        // that no longer exists, and nothing else would ever mention it again.
        using var store = Store();
        var ledger = new IngestionLedger(store.Connection);

        await store.UpsertAsync([
            new EmbeddedChunk(new Chunk("gone#1", "src/Gone.cs", 1, 10, "VanishedMarker"), [1f, 0f]),
            new EmbeddedChunk(new Chunk("kept#1", "src/Kept.cs", 1, 10, "KeptMarker"), [0f, 1f]),
        ]);
        ledger.Record("src/Gone.cs", "h1", 1);
        ledger.Record("src/Kept.cs", "h2", 1);

        ledger.Forget(["src/Gone.cs"]);

        Assert.Empty(await store.SearchTextAsync("VanishedMarker", 5));
        Assert.Single(await store.SearchTextAsync("KeptMarker", 5));
        Assert.Single(ledger.Known());
    }

    [Fact]
    public void Clearing_the_ledger_makes_everything_look_unindexed()
    {
        // Otherwise an ingest after purging the index reports every file as
        // already done and writes nothing at all.
        using var store = Store();
        var ledger = new IngestionLedger(store.Connection);

        ledger.Record("src/A.cs", "hash", 3);
        ledger.Clear();

        Assert.Empty(ledger.Known());
    }

    [Fact]
    public void An_empty_ledger_reports_nothing_rather_than_failing()
    {
        using var store = Store();
        Assert.Empty(new IngestionLedger(store.Connection).Known());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
