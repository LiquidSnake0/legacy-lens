using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LegacyLens.Api.Ingestion;

/// <summary>
/// Remembers which files have been indexed, and at what content.
///
/// Embedding is the expensive half of this system by an enormous margin: on a
/// CPU it runs at roughly two chunks a second, and neither batching nor
/// concurrency improves that, because a single embedding already saturates
/// every core. The only way to make re-indexing fast is to not do it.
///
/// Files are keyed by a hash of their content rather than a timestamp. A
/// checkout, a branch switch or a restored backup all rewrite modification
/// times without changing a line, and would otherwise force a full re-index.
/// </summary>
public class IngestionLedger
{
    private readonly SqliteConnection _connection;

    private readonly string _workspace;

    public IngestionLedger(SqliteConnection connection, string workspace = "default")
    {
        _connection = connection;
        _workspace = workspace;

        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS indexed_files (
                path         TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                chunk_count  INTEGER NOT NULL,
                indexed_at   TEXT NOT NULL,
                workspace_id TEXT NOT NULL DEFAULT 'default',
                PRIMARY KEY (workspace_id, path)
            );
            """;
        create.ExecuteNonQuery();
    }

    public static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Path to content hash, for everything indexed so far.</summary>
    public Dictionary<string, string> Known()
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path, content_hash FROM indexed_files WHERE workspace_id = $workspace";
        command.Parameters.AddWithValue("$workspace", _workspace);

        using var reader = command.ExecuteReader();
        while (reader.Read()) known[reader.GetString(0)] = reader.GetString(1);

        return known;
    }

    public void Record(string path, string hash, int chunks)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO indexed_files (path, content_hash, chunk_count, indexed_at, workspace_id)
            VALUES ($path, $hash, $chunks, $at, $workspace)
            ON CONFLICT(workspace_id, path) DO UPDATE SET
                content_hash = excluded.content_hash,
                chunk_count  = excluded.chunk_count,
                indexed_at   = excluded.indexed_at;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$workspace", _workspace);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$chunks", chunks);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes a file and its chunks from the index.
    ///
    /// Deleted files have to be dropped explicitly. Nothing else would ever
    /// mention them again, so their chunks would sit in the index forever and
    /// answer questions with code that no longer exists.
    /// </summary>
    public void Forget(IEnumerable<string> paths)
    {
        using var transaction = _connection.BeginTransaction();

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM chunks_fts WHERE id IN (
                SELECT id FROM chunks WHERE file_path = $path AND workspace_id = $workspace);
            DELETE FROM chunks        WHERE file_path = $path AND workspace_id = $workspace;
            DELETE FROM indexed_files WHERE path = $path AND workspace_id = $workspace;
            """;
        command.Parameters.AddWithValue("$workspace", _workspace);
        var parameter = command.Parameters.Add("$path", SqliteType.Text);

        foreach (var path in paths)
        {
            parameter.Value = path;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Only this workspace. Another project's ledger is not ours to drop.</summary>
    public void Clear()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM indexed_files WHERE workspace_id = $workspace";
        command.Parameters.AddWithValue("$workspace", _workspace);
        command.ExecuteNonQuery();
    }
}
