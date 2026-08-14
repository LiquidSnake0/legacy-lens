using Microsoft.Data.Sqlite;

namespace LegacyLens.Api.Storage;

public interface IVectorStore
{
    /// <summary>
    /// The underlying connection, so that the ingestion ledger lives in the
    /// same file. Two databases that must agree with each other are two
    /// databases that will eventually disagree.
    /// </summary>
    SqliteConnection Connection { get; }

    Task UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int topK, CancellationToken ct = default);

    /// <summary>
    /// Full-text search over the same chunks.
    ///
    /// Vector search is weak on rare identifiers: someone typing PriceEngine
    /// wants that exact token, and an embedding has no reason to favour an
    /// exact match on a proper noun it never saw in training.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchTextAsync(string query, int topK, CancellationToken ct = default);

    /// <summary>
    /// The text of one indexed chunk, so a citation can be opened and read.
    ///
    /// Served from the index rather than from the file on disk: the stored
    /// text is what the model was actually given, and the file may well have
    /// changed since. Showing the current file would let a citation point at
    /// something the answer never saw.
    /// </summary>
    Task<Chunk?> ExcerptAsync(string filePath, int startLine, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <summary>
/// Vectors in a SQLite file, searched by brute-force cosine scan.
///
/// The index is one file you can copy, inspect and delete, which matters more
/// than search latency at this scale. See the README for where that stops being
/// true and what to swap in.
/// </summary>
public class SqliteVectorStore : IVectorStore, IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteConnection Connection => _connection;

    public SqliteVectorStore(IConfiguration config)
    {
        var path = config["INDEX_PATH"] ?? "index.db";
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS chunks (
                id         TEXT PRIMARY KEY,
                file_path  TEXT NOT NULL,
                start_line INTEGER NOT NULL,
                end_line   INTEGER NOT NULL,
                content    TEXT NOT NULL,
                embedding  BLOB NOT NULL
            );

            -- Full-text index over the same rows. FTS5 ships with SQLite, so
            -- lexical search costs no new dependency.
            --
            -- The default tokeniser splits on non-alphanumerics, which turns
            -- PriceEngine into one token and Price_Engine into two. That suits
            -- code: an identifier searched for is usually typed the way it is
            -- written.
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                id UNINDEXED,
                content,
                tokenize = 'unicode61'
            );
            """;
        create.ExecuteNonQuery();

        Backfill();
    }

    /// <summary>
    /// Fills the full-text index from chunks already stored.
    ///
    /// An index built before this table existed holds vectors and no text, and
    /// lexical search over it would silently return nothing. Re-indexing the
    /// repository would work too, and costs the embedding of every chunk again;
    /// this is the same result in a few milliseconds.
    /// </summary>
    private void Backfill()
    {
        using var count = _connection.CreateCommand();
        count.CommandText =
            "SELECT (SELECT COUNT(*) FROM chunks) - (SELECT COUNT(*) FROM chunks_fts)";

        if (Convert.ToInt32(count.ExecuteScalar()) <= 0) return;

        using var fill = _connection.CreateCommand();
        fill.CommandText = """
            INSERT INTO chunks_fts (id, content)
            SELECT id, content FROM chunks
            WHERE id NOT IN (SELECT id FROM chunks_fts);
            """;
        fill.ExecuteNonQuery();
    }

    public async Task UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        // One transaction for the batch: SQLite commits per statement otherwise,
        // which turns a 20k-row insert into 20k fsyncs.
        await using var transaction = _connection.BeginTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO chunks (id, file_path, start_line, end_line, content, embedding)
            VALUES ($id, $path, $start, $end, $content, $embedding)
            ON CONFLICT(id) DO UPDATE SET
                file_path = excluded.file_path,
                start_line = excluded.start_line,
                end_line = excluded.end_line,
                content = excluded.content,
                embedding = excluded.embedding;
            """;

        var id        = command.Parameters.Add("$id", SqliteType.Text);
        var path      = command.Parameters.Add("$path", SqliteType.Text);
        var start     = command.Parameters.Add("$start", SqliteType.Integer);
        var end       = command.Parameters.Add("$end", SqliteType.Integer);
        var content   = command.Parameters.Add("$content", SqliteType.Text);
        var embedding = command.Parameters.Add("$embedding", SqliteType.Blob);

        foreach (var item in chunks)
        {
            ct.ThrowIfCancellationRequested();
            id.Value        = item.Chunk.Id;
            path.Value      = item.Chunk.FilePath;
            start.Value     = item.Chunk.StartLine;
            end.Value       = item.Chunk.EndLine;
            content.Value   = item.Chunk.Content;
            embedding.Value = ToBytes(item.Embedding);
            await command.ExecuteNonQueryAsync(ct);
        }

        // FTS5 has no ON CONFLICT, so a re-index would otherwise accumulate
        // duplicate rows for the same chunk id.
        await using var fts = _connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = """
            DELETE FROM chunks_fts WHERE id = $id;
            INSERT INTO chunks_fts (id, content) VALUES ($id, $content);
            """;
        var ftsId = fts.Parameters.Add("$id", SqliteType.Text);
        var ftsContent = fts.Parameters.Add("$content", SqliteType.Text);

        foreach (var item in chunks)
        {
            ct.ThrowIfCancellationRequested();
            ftsId.Value = item.Chunk.Id;
            ftsContent.Value = item.Chunk.Content;
            await fts.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        float[] query, int topK, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();

        await using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT id, file_path, start_line, end_line, content, embedding FROM chunks";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var blob = (byte[])reader["embedding"];
            var score = VectorMath.CosineSimilarity(query, ToFloats(blob));

            hits.Add(new SearchHit(
                new Chunk(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetString(4)),
                score));
        }

        return hits.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    public async Task<IReadOnlyList<SearchHit>> SearchTextAsync(
        string query, int topK, CancellationToken ct = default)
    {
        var terms = Tokenise(query);
        if (terms.Length == 0) return [];

        var hits = new List<SearchHit>();

        await using var command = _connection.CreateCommand();
        // bm25() returns a negative number, more negative meaning a better
        // match. Negating it gives the usual "higher is better" ordering.
        command.CommandText = """
            SELECT c.id, c.file_path, c.start_line, c.end_line, c.content,
                   -bm25(chunks_fts) AS relevance
            FROM chunks_fts
            JOIN chunks c ON c.id = chunks_fts.id
            WHERE chunks_fts MATCH $query
            ORDER BY relevance DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", string.Join(" OR ", terms));
        command.Parameters.AddWithValue("$limit", topK);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                hits.Add(new SearchHit(
                    new Chunk(reader.GetString(0), reader.GetString(1),
                              reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4)),
                    (float)reader.GetDouble(5)));
            }
        }
        catch (SqliteException)
        {
            // A malformed MATCH expression is a bad question, not a broken
            // store. Returning nothing lets the vector half answer alone.
            return [];
        }

        return hits;
    }

    public async Task<Chunk?> ExcerptAsync(
        string filePath, int startLine, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, file_path, start_line, end_line, content
            FROM chunks
            WHERE file_path = $path AND start_line = $start
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$start", startLine);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new Chunk(reader.GetString(0), reader.GetString(1),
                         reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4));
    }

    /// <summary>
    /// Splits a question into terms FTS5 will accept.
    ///
    /// User text reaches MATCH directly otherwise, and characters like " ( -
    /// and * are query syntax there: a question containing any of them would
    /// throw rather than search.
    /// </summary>
    internal static string[] Tokenise(string query) =>
        new string(query.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            // Single characters match almost everything and rank nothing.
            .Where(term => term.Length > 1)
            .Select(term => $"\"{term}\"")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM chunks; DELETE FROM chunks_fts;";
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chunks";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] ToFloats(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    public void Dispose() => _connection.Dispose();
}
