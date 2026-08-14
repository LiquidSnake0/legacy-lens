using System.Diagnostics;
using LegacyLens.Analysis;
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
        var ledger = new IngestionLedger(_store.Connection);
        var known = ledger.Known();

        var files = _walker.Walk(rootPath).ToList();
        _log.LogInformation("Found {Count} files under {Root}", files.Count, rootPath);

        var chunks = new List<Chunk>();
        var pending = new List<(string Path, string Hash, int Chunks)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unchanged = 0;
        var generated = 0;

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

            // Generated code is not indexed. A WSDL proxy answers no question
            // anyone asks, and embedding twelve thousand lines of it costs
            // minutes that buy nothing.
            if (CodeMetrics.LooksGenerated(file, content, false))
            {
                generated++;
                continue;
            }

            // Paths are stored relative to the root: an index built in a
            // container has to stay readable when opened outside it.
            var relative = Path.GetRelativePath(rootPath, file);
            seen.Add(relative);

            var hash = IngestionLedger.Hash(content);
            if (known.TryGetValue(relative, out var previous) && previous == hash)
            {
                unchanged++;
                continue;
            }

            var split = _chunker.Split(relative, content);
            chunks.AddRange(split);
            pending.Add((relative, hash, split.Count));
        }

        // Files the ledger knows but the walk no longer found: deleted, renamed
        // or newly excluded. Their chunks would otherwise answer questions with
        // code that no longer exists.
        var vanished = known.Keys.Where(path => !seen.Contains(path)).ToList();
        if (vanished.Count > 0)
        {
            ledger.Forget(vanished);
            _log.LogInformation("Dropped {Count} file(s) no longer present", vanished.Count);
        }

        _log.LogInformation(
            "{Changed} file(s) to index, {Unchanged} unchanged, {Generated} generated and skipped",
            pending.Count, unchanged, generated);

        if (chunks.Count == 0)
        {
            stopwatch.Stop();
            return new IngestResponse(files.Count, 0, stopwatch.ElapsedMilliseconds);
        }

        _log.LogInformation("Embedding {Count} chunks, this is the slow part", chunks.Count);

        var vectors = await _embeddings.EmbedBatchAsync(
            chunks.Select(c => c.EmbeddingText).ToList(), ct);

        await _store.UpsertAsync(
            chunks.Zip(vectors, (chunk, vector) => new EmbeddedChunk(chunk, vector)).ToList(), ct);

        // Recorded only once the chunks are stored. Crashing mid-run then leaves
        // those files marked unindexed, so the next run redoes them, rather than
        // marking them done and leaving a silent hole in the index.
        foreach (var (path, hash, count) in pending) ledger.Record(path, hash, count);

        stopwatch.Stop();
        _log.LogInformation(
            "Indexed {Chunks} chunks from {Files} files in {Seconds}s",
            chunks.Count, files.Count, stopwatch.Elapsed.TotalSeconds);

        return new IngestResponse(files.Count, chunks.Count, stopwatch.ElapsedMilliseconds);
    }
}
