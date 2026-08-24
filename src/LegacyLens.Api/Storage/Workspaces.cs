using Microsoft.Data.Sqlite;

namespace LegacyLens.Api.Storage;

/// <summary>One indexed project.</summary>
public record Workspace(string Id, string Name, string RootPath, DateTimeOffset CreatedAt, int Chunks);

/// <summary>
/// Which project a chunk belongs to.
///
/// Until now the index held one project, so a second one meant deleting the
/// first or answering questions about a mixture of the two. The identifier is
/// carried on every chunk and on the ledger beside it, and every read is
/// scoped, so two projects in the same file cannot see each other.
/// </summary>
public class Workspaces
{
    /// <summary>
    /// Where an index built before workspaces existed ends up.
    ///
    /// The alternative was to refuse to open an old file, which would throw
    /// away an index that took hours to build and is still perfectly good.
    /// </summary>
    public const string Default = "default";

    private readonly SqliteConnection _connection;

    public Workspaces(SqliteConnection connection)
    {
        _connection = connection;
        Migrate();
    }

    /// <summary>
    /// Adds what is missing and touches nothing else.
    ///
    /// Written as add-if-absent rather than as a numbered migration because
    /// there is exactly one version to come from. A real migration table earns
    /// its place at the second one, not the first.
    /// </summary>
    private void Migrate()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS workspaces (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                root_path  TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """);

        // The ledger creates this too, but only when an ingestion runs. A
        // workspace created and deleted without ever being indexed would
        // otherwise hit a table that does not exist yet, which is the most
        // ordinary sequence there is.
        Execute($"""
            CREATE TABLE IF NOT EXISTS indexed_files (
                path         TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                chunk_count  INTEGER NOT NULL,
                indexed_at   TEXT NOT NULL,
                workspace_id TEXT NOT NULL DEFAULT '{Default}',
                PRIMARY KEY (workspace_id, path)
            );
            """);

        AddColumnIfAbsent("chunks", "workspace_id");
        RebuildChunksIfSingleKeyed();
        RebuildTextIndexIfUnscoped();
        RebuildLedgerIfSingleKeyed();

        Execute("CREATE INDEX IF NOT EXISTS idx_chunks_workspace ON chunks(workspace_id);");

        // An index that already holds chunks gets a workspace to belong to,
        // named for what it is rather than left nameless.
        if (Count(Default) > 0 && Find(Default) is null)
        {
            Execute("""
                INSERT INTO workspaces (id, name, root_path, created_at)
                VALUES ($id, 'Indexed before workspaces existed', '', $now);
                """,
                ("$id", Default), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        }
    }

    /// <summary>
    /// A chunk id is its file path and start line, so it is unique within a
    /// workspace and not across two. Keyed on the id alone, a second workspace
    /// indexing a path of the same name overwrites the first workspace's chunk
    /// instead of sitting beside it, and the isolation this milestone exists
    /// for would not hold. SQLite cannot alter a primary key, so the table is
    /// rebuilt once.
    /// </summary>
    private void RebuildChunksIfSingleKeyed()
    {
        if (KeyedOnWorkspace("chunks")) return;
        if (Columns("chunks").Count == 0) return;

        Execute($"""
            CREATE TABLE chunks_rebuilt (
                id           TEXT NOT NULL,
                file_path    TEXT NOT NULL,
                start_line   INTEGER NOT NULL,
                end_line     INTEGER NOT NULL,
                content      TEXT NOT NULL,
                embedding    BLOB NOT NULL,
                workspace_id TEXT NOT NULL DEFAULT '{Default}',
                PRIMARY KEY (workspace_id, id)
            );

            INSERT INTO chunks_rebuilt
            SELECT id, file_path, start_line, end_line, content, embedding, workspace_id
            FROM chunks;

            DROP TABLE chunks;
            ALTER TABLE chunks_rebuilt RENAME TO chunks;
            """);
    }

    /// <summary>
    /// The full-text rows carried an id and no workspace, so the same path
    /// indexed twice left one searchable row holding whichever text was written
    /// last. Rebuilt from the chunks, which by this point know where they
    /// belong. FTS5 has no ALTER, so the table is dropped and refilled rather
    /// than amended.
    /// </summary>
    private void RebuildTextIndexIfUnscoped()
    {
        var columns = Columns("chunks_fts");
        if (columns.Count == 0) return;
        if (columns.Any(c => c.Name.Equals("workspace_id", StringComparison.OrdinalIgnoreCase))) return;

        Execute("""
            DROP TABLE chunks_fts;

            CREATE VIRTUAL TABLE chunks_fts USING fts5(
                id UNINDEXED,
                workspace_id UNINDEXED,
                content,
                tokenize = 'unicode61'
            );

            INSERT INTO chunks_fts (id, workspace_id, content)
            SELECT id, workspace_id, content FROM chunks;
            """);
    }

    /// <summary>Whether the table's primary key already includes the workspace.</summary>
    private bool KeyedOnWorkspace(string table) =>
        Columns(table).Any(c =>
            c.Name.Equals("workspace_id", StringComparison.OrdinalIgnoreCase) && c.IsKey);

    private List<(string Name, bool IsKey)> Columns(string table)
    {
        var columns = new List<(string Name, bool IsKey)>();

        using var info = _connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table});";
        using var reader = info.ExecuteReader();
        while (reader.Read()) columns.Add((reader.GetString(1), reader.GetInt32(5) > 0));

        return columns;
    }

    /// <summary>
    /// The ledger keyed a file by its path alone, which two workspaces
    /// indexing the same relative path would collide on. SQLite cannot alter a
    /// primary key, so the table is rebuilt once and the existing rows land in
    /// the default workspace.
    /// </summary>
    private void RebuildLedgerIfSingleKeyed()
    {
        var columns = Columns("indexed_files");

        // No table yet means the ledger will create it in the right shape.
        if (columns.Count == 0) return;
        if (KeyedOnWorkspace("indexed_files")) return;

        var carriesWorkspace = columns.Any(c =>
            c.Name.Equals("workspace_id", StringComparison.OrdinalIgnoreCase));

        var source = carriesWorkspace
            ? "path, content_hash, chunk_count, indexed_at, workspace_id"
            : $"path, content_hash, chunk_count, indexed_at, '{Default}'";

        Execute($"""
            CREATE TABLE indexed_files_rebuilt (
                path         TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                chunk_count  INTEGER NOT NULL,
                indexed_at   TEXT NOT NULL,
                workspace_id TEXT NOT NULL DEFAULT '{Default}',
                PRIMARY KEY (workspace_id, path)
            );

            INSERT INTO indexed_files_rebuilt
            SELECT {source} FROM indexed_files;

            DROP TABLE indexed_files;
            ALTER TABLE indexed_files_rebuilt RENAME TO indexed_files;
            """);
    }

    private void AddColumnIfAbsent(string table, string column)
    {
        using var columns = _connection.CreateCommand();
        columns.CommandText = $"PRAGMA table_info({table});";

        using var reader = columns.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        }

        reader.Close();
        Execute($"ALTER TABLE {table} ADD COLUMN {column} TEXT NOT NULL DEFAULT '{Default}';");
    }

    public Workspace Create(string name, string rootPath)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;

        Execute("""
            INSERT INTO workspaces (id, name, root_path, created_at)
            VALUES ($id, $name, $root, $now);
            """,
            ("$id", id), ("$name", name), ("$root", rootPath), ("$now", now.ToString("O")));

        return new Workspace(id, name, rootPath, now, 0);
    }

    public IReadOnlyList<Workspace> All()
    {
        var found = new List<Workspace>();

        using var command = _connection.CreateCommand();
        // Counted by joining rather than by keeping a running total: a stored
        // count is a second copy of a fact the chunks already carry, and the
        // two disagree the first time an ingestion fails halfway.
        command.CommandText = """
            SELECT w.id, w.name, w.root_path, w.created_at, COUNT(c.id)
            FROM workspaces w
            LEFT JOIN chunks c ON c.workspace_id = w.id
            GROUP BY w.id, w.name, w.root_path, w.created_at
            ORDER BY w.created_at DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            found.Add(new Workspace(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)), reader.GetInt32(4)));
        }

        return found;
    }

    public Workspace? Find(string id) =>
        All().FirstOrDefault(w => w.Id.Equals(id, StringComparison.Ordinal));

    /// <summary>
    /// Removes the workspace and everything indexed under it.
    ///
    /// The chunks go first. A workspace row deleted on its own would leave
    /// chunks nothing can name, which is worse than either outcome.
    /// </summary>
    public bool Delete(string id)
    {
        if (Find(id) is null) return false;

        Execute("DELETE FROM chunks_fts WHERE workspace_id = $id;", ("$id", id));
        Execute("DELETE FROM chunks WHERE workspace_id = $id;", ("$id", id));
        Execute("DELETE FROM indexed_files WHERE workspace_id = $id;", ("$id", id));
        Execute("DELETE FROM workspaces WHERE id = $id;", ("$id", id));
        return true;
    }

    public int Count(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chunks WHERE workspace_id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
