using Microsoft.Data.Sqlite;

namespace LegacyLens.Api.Storage;

public interface IVectorStore
{
    Task UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int topK, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <summary>
/// Vectors in a SQLite file, searched by brute-force cosine scan.
///
/// The index is one file you can copy, inspect and delete — which matters more
/// than search latency at this scale. See the README for where that stops being
/// true and what to swap in.
/// </summary>
public class SqliteVectorStore : IVectorStore, IDisposable
{
    private readonly SqliteConnection _connection;

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
            """;
        create.ExecuteNonQuery();
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

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM chunks";
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
