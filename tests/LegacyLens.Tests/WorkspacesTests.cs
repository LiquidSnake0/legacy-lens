using LegacyLens.Api;
using LegacyLens.Api.Ingestion;
using LegacyLens.Api.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace LegacyLens.Tests;

/// <summary>
/// Two projects in one index file.
///
/// The tests that matter are the ones where the two collide: a chunk id is its
/// file path and start line, so two workspaces that each hold a src/A.cs
/// produce the same id for two different pieces of code. Isolation that only
/// works while the paths differ is not isolation, and nothing but running it
/// against a real file says which of the two is stored.
/// </summary>
public class WorkspacesTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"lens-workspaces-{Guid.NewGuid():N}.db");

    private SqliteVectorStore Store()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["INDEX_PATH"] = _path })
            .Build();
        return new SqliteVectorStore(config);
    }

    private static EmbeddedChunk Chunk(string content, float[] vector, string path = "src/A.cs") =>
        new(new Chunk($"{path}:1", path, 1, 20, content), vector);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_same_path_in_two_workspaces_is_two_different_chunks()
    {
        using var store = Store();
        await store.UpsertAsync([Chunk("class Invoicing { }", [1f, 0f])], "alpha");
        await store.UpsertAsync([Chunk("class Payroll { }", [1f, 0f])], "beta");

        Assert.Equal("class Invoicing { }", (await store.ExcerptAsync("src/A.cs", 1, "alpha"))!.Content);
        Assert.Equal("class Payroll { }", (await store.ExcerptAsync("src/A.cs", 1, "beta"))!.Content);
        Assert.Equal(1, await store.CountAsync("alpha"));
        Assert.Equal(1, await store.CountAsync("beta"));
    }

    [Fact]
    public async Task A_vector_search_never_returns_another_workspaces_code()
    {
        using var store = Store();
        await store.UpsertAsync([Chunk("class Invoicing { }", [1f, 0f])], "alpha");
        await store.UpsertAsync([Chunk("class Payroll { }", [1f, 0f], "src/B.cs")], "beta");

        var hits = await store.SearchAsync([1f, 0f], 10, "alpha");

        Assert.Equal("class Invoicing { }", Assert.Single(hits).Chunk.Content);
    }

    [Fact]
    public async Task A_text_search_never_returns_another_workspaces_code()
    {
        // The full-text index is one virtual table shared by every workspace,
        // so this is the search most likely to leak.
        using var store = Store();
        await store.UpsertAsync([Chunk("class Invoicing { }", [1f, 0f])], "alpha");
        await store.UpsertAsync([Chunk("class Payroll { }", [1f, 0f])], "beta");

        Assert.Empty(await store.SearchTextAsync("Payroll", 10, "alpha"));
        Assert.Single(await store.SearchTextAsync("Payroll", 10, "beta"));
    }

    [Fact]
    public async Task Clearing_one_workspace_leaves_the_other_whole()
    {
        using var store = Store();
        await store.UpsertAsync([Chunk("class Invoicing { }", [1f, 0f])], "alpha");
        await store.UpsertAsync([Chunk("class Payroll { }", [1f, 0f])], "beta");

        await store.ClearAsync("alpha");

        Assert.Equal(0, await store.CountAsync("alpha"));
        Assert.Equal(1, await store.CountAsync("beta"));
        Assert.Single(await store.SearchTextAsync("Payroll", 10, "beta"));
    }

    [Fact]
    public async Task Re_indexing_a_workspace_updates_its_own_row_rather_than_adding_one()
    {
        using var store = Store();
        await store.UpsertAsync([Chunk("class Old { }", [1f, 0f])], "alpha");
        await store.UpsertAsync([Chunk("class New { }", [1f, 0f])], "alpha");

        Assert.Equal(1, await store.CountAsync("alpha"));
        Assert.Equal("class New { }", (await store.ExcerptAsync("src/A.cs", 1, "alpha"))!.Content);
        Assert.Empty(await store.SearchTextAsync("Old", 10, "alpha"));
    }

    [Fact]
    public void The_ledger_tracks_the_same_path_separately_per_workspace()
    {
        using var store = Store();
        new IngestionLedger(store.Connection, "alpha").Record("src/A.cs", "hash-alpha", 4);
        new IngestionLedger(store.Connection, "beta").Record("src/A.cs", "hash-beta", 7);

        Assert.Equal("hash-alpha", new IngestionLedger(store.Connection, "alpha").Known()["src/A.cs"]);
        Assert.Equal("hash-beta", new IngestionLedger(store.Connection, "beta").Known()["src/A.cs"]);
    }

    [Fact]
    public async Task Deleting_a_workspace_takes_its_chunks_its_text_and_its_ledger()
    {
        using var store = Store();
        var workspaces = new Workspaces(store.Connection);
        var alpha = workspaces.Create("Alpha", "/srv/alpha");

        await store.UpsertAsync([Chunk("class Invoicing { }", [1f, 0f])], alpha.Id);
        new IngestionLedger(store.Connection, alpha.Id).Record("src/A.cs", "hash", 1);

        Assert.True(workspaces.Delete(alpha.Id));

        Assert.Null(workspaces.Find(alpha.Id));
        Assert.Equal(0, await store.CountAsync(alpha.Id));
        Assert.Empty(await store.SearchTextAsync("Invoicing", 10, alpha.Id));
        Assert.Empty(new IngestionLedger(store.Connection, alpha.Id).Known());
    }

    [Fact]
    public async Task A_workspace_counts_the_chunks_it_holds()
    {
        using var store = Store();
        var workspaces = new Workspaces(store.Connection);
        var alpha = workspaces.Create("Alpha", "/srv/alpha");

        await store.UpsertAsync(
            [Chunk("class A { }", [1f, 0f]), Chunk("class B { }", [0f, 1f], "src/B.cs")], alpha.Id);

        Assert.Equal(2, workspaces.Find(alpha.Id)!.Chunks);
    }

    [Fact]
    public void A_workspace_can_be_deleted_before_anything_was_ever_indexed_into_it()
    {
        // The ledger table is created by an ingestion, so a workspace made and
        // dropped without one used to reach a table that did not exist. Found
        // against a running instance rather than here, because the other tests
        // all index something first and so create the table on the way past.
        using var store = Store();
        var workspaces = new Workspaces(store.Connection);
        var alpha = workspaces.Create("Alpha", "/srv/alpha");

        Assert.True(workspaces.Delete(alpha.Id));
        Assert.Null(workspaces.Find(alpha.Id));
    }

    [Fact]
    public void Deleting_a_workspace_that_was_never_created_is_refused_rather_than_ignored()
    {
        using var store = Store();
        Assert.False(new Workspaces(store.Connection).Delete("never-existed"));
    }

    /// <summary>
    /// Writes an index in the shape it had before workspaces existed: chunks
    /// keyed on the id alone, a full-text table with no workspace column, and a
    /// ledger keyed on the path. Hand-built rather than obtained by checking out
    /// the old code, because the migration has to survive files that were
    /// written months ago and no longer have any code to produce them.
    /// </summary>
    private void WriteIndexInTheOldShape()
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE chunks (
                id         TEXT PRIMARY KEY,
                file_path  TEXT NOT NULL,
                start_line INTEGER NOT NULL,
                end_line   INTEGER NOT NULL,
                content    TEXT NOT NULL,
                embedding  BLOB NOT NULL
            );

            CREATE VIRTUAL TABLE chunks_fts USING fts5(
                id UNINDEXED, content, tokenize = 'unicode61');

            CREATE TABLE indexed_files (
                path         TEXT PRIMARY KEY,
                content_hash TEXT NOT NULL,
                chunk_count  INTEGER NOT NULL,
                indexed_at   TEXT NOT NULL
            );

            INSERT INTO chunks VALUES
                ('src/A.cs:1', 'src/A.cs', 1, 20, 'class Invoicing { }', x'0000803f00000000');
            INSERT INTO chunks_fts (id, content) VALUES ('src/A.cs:1', 'class Invoicing { }');
            INSERT INTO indexed_files VALUES ('src/A.cs', 'hash-a', 1, '2026-01-01T00:00:00+00:00');
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task An_index_built_before_workspaces_existed_is_carried_into_the_default_one()
    {
        // Refusing to open it was the alternative, and it throws away an index
        // that took hours to build and is still perfectly good.
        WriteIndexInTheOldShape();

        using var store = Store();
        var workspaces = new Workspaces(store.Connection);

        Assert.Equal(1, await store.CountAsync(Workspaces.Default));
        Assert.Equal("class Invoicing { }",
            (await store.ExcerptAsync("src/A.cs", 1, Workspaces.Default))!.Content);
        Assert.Single(await store.SearchTextAsync("Invoicing", 10, Workspaces.Default));
        Assert.Equal("hash-a",
            new IngestionLedger(store.Connection, Workspaces.Default).Known()["src/A.cs"]);
        Assert.NotNull(workspaces.Find(Workspaces.Default));
    }

    [Fact]
    public async Task A_migrated_index_can_take_a_second_project_at_the_same_paths()
    {
        // The migration is only worth anything if the file it produces behaves
        // like one that was created new. Same path, different code, both kept.
        WriteIndexInTheOldShape();

        using var store = Store();
        _ = new Workspaces(store.Connection);

        await store.UpsertAsync([Chunk("class Payroll { }", [1f, 0f])], "beta");

        Assert.Equal("class Invoicing { }",
            (await store.ExcerptAsync("src/A.cs", 1, Workspaces.Default))!.Content);
        Assert.Equal("class Payroll { }", (await store.ExcerptAsync("src/A.cs", 1, "beta"))!.Content);
        Assert.Empty(await store.SearchTextAsync("Payroll", 10, Workspaces.Default));
    }

    [Fact]
    public void Migrating_twice_is_the_same_as_migrating_once()
    {
        // The API builds this on every start, so the second run has to be a
        // no-op rather than a second rebuild of tables it already fixed.
        WriteIndexInTheOldShape();

        using var store = Store();
        _ = new Workspaces(store.Connection);
        var workspaces = new Workspaces(store.Connection);

        Assert.Equal(1, workspaces.Count(Workspaces.Default));
        Assert.Single(workspaces.All());
    }
}
