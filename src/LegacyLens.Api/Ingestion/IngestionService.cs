using System.Diagnostics;
using LegacyLens.Api.Embeddings;
using LegacyLens.Api.Storage;

namespace LegacyLens.Api.Ingestion;

public class IngestionService
{
    private readonly SourceWalker _walker;
    private readonly CodeChunker _chunker;
    private readonly IEmbeddingClient _embeddings;
    private readonly IVectorStore _store;
    private readonly ILogger<IngestionService> _log;

    public IngestionService(
        SourceWalker walker,
        CodeChunker chunker,
        IEmbeddingClient embeddings,
        IVectorStore store,
        ILogger<IngestionService> log)
    {
        _walker = walker;
        _chunker = chunker;
        _embeddings = embeddings;
        _store = store;
        _log = log;
    }

    public async Task<IngestResponse> IngestAsync(string rootPath, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = _walker.Walk(rootPath).ToList();
        _log.LogInformation("Found {Count} files under {Root}", files.Count, rootPath);

        var chunks = new List<Chunk>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Skipped {File}: {Reason}", file, ex.Message);
                continue;
            }

            // Paths are stored relative to the root: an index built in a
            // container has to stay readable when opened outside it.
            var relative = Path.GetRelativePath(rootPath, file);
            chunks.AddRange(_chunker.Split(relative, content));
        }

        _log.LogInformation("Embedding {Count} chunks — this is the slow part", chunks.Count);

        var vectors = await _embeddings.EmbedBatchAsync(
            chunks.Select(c => c.EmbeddingText).ToList(), ct);

        await _store.UpsertAsync(
            chunks.Zip(vectors, (chunk, vector) => new EmbeddedChunk(chunk, vector)).ToList(), ct);

        stopwatch.Stop();
        _log.LogInformation(
            "Indexed {Chunks} chunks from {Files} files in {Seconds}s",
            chunks.Count, files.Count, stopwatch.Elapsed.TotalSeconds);

        return new IngestResponse(files.Count, chunks.Count, stopwatch.ElapsedMilliseconds);
    }
}
