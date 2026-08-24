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

    /// <summary>
    /// Indexes a directory.
    ///
    /// <paramref name="progress"/> is reported against the files that actually
    /// need work, not against every file found: on a re-index most are
    /// unchanged and counting them would show a bar that jumps to 90% and then
    /// sits still for an hour.
    /// </summary>
    public async Task<IngestResponse> IngestAsync(string rootPath,
        string workspace = Workspaces.Default,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var ledger = new IngestionLedger(_store.Connection, workspace);
        var known = ledger.Known();

        var files = _walker.Walk(rootPath).ToList();
        _log.LogInformation("Found {Count} files under {Root}", files.Count, rootPath);

        var pending = new List<(string Absolute, string Relative)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unchanged = 0;
        var generated = 0;

        // First pass: decide what needs work. It reads every file and embeds
        // none of them, so it costs seconds on an estate that takes hours to
        // index, and the count it produces is what the second pass reports
        // progress against.
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

            pending.Add((file, relative));
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

        if (pending.Count == 0)
        {
            stopwatch.Stop();
            progress?.Report(new IngestionProgress(0, 0, 0, null));
            return new IngestResponse(files.Count, 0, stopwatch.ElapsedMilliseconds);
        }

        progress?.Report(new IngestionProgress(pending.Count, 0, 0, null));

        _log.LogInformation(
            "Embedding {Count} file(s), this is the slow part", pending.Count);

        // Second pass: one file at a time, embedded, stored and recorded before
        // the next one starts. A two-hour run that dies at 95% used to lose the
        // whole thing, because every vector was held in memory until a single
        // write at the end. Now the next run picks up where this one stopped and
        // the cost of a crash is the file in flight.
        //
        // This gives up nothing: batching the embedding calls was measured and
        // made it slower, since one embedding already saturates every core.
        var embedding = Stopwatch.StartNew();
        var indexed = 0;
        var done = 0;

        foreach (var (absolute, relative) in pending)
        {
            ct.ThrowIfCancellationRequested();

            // Re-read rather than hold the first pass's content in memory: an
            // estate large enough to need resuming is large enough that keeping
            // all of it resident is the next thing to break.
            string content;
            try
            {
                content = await File.ReadAllTextAsync(absolute, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Skipped {File}: {Reason}", absolute, ex.Message);
                continue;
            }

            var split = _chunker.Split(relative, content);

            if (split.Count > 0)
            {
                var vectors = await _embeddings.EmbedBatchAsync(
                    split.Select(c => c.EmbeddingText).ToList(), ct);

                await _store.UpsertAsync(
                    split.Zip(vectors, (chunk, vector) => new EmbeddedChunk(chunk, vector)).ToList(),
                    workspace, ct);
            }

            // Recorded only once the chunks are stored, and hashed from the
            // content this pass actually indexed. Crashing before this line
            // leaves the file marked unindexed, so the next run redoes it,
            // rather than marking it done and leaving a silent hole in the
            // index. Redoing a file is idempotent; a hole is invisible.
            ledger.Record(relative, IngestionLedger.Hash(content), split.Count);

            indexed += split.Count;
            done++;
            progress?.Report(new IngestionProgress(pending.Count, done, indexed, relative));

            // A run this long that reports nothing is indistinguishable from a
            // hung one. The remaining time is extrapolated from the files done
            // so far and says so: it is the only number here that is not measured.
            var remaining = TimeSpan.FromTicks(
                embedding.Elapsed.Ticks / done * (pending.Count - done));

            _log.LogInformation(
                "[{Done}/{Total}] {Path}: {Chunks} chunk(s), {Rate} chunk/s, ~{Remaining} left at this rate",
                done, pending.Count, relative, split.Count,
                (indexed / embedding.Elapsed.TotalSeconds).ToString("F1"),
                remaining.ToString(@"hh\:mm\:ss"));
        }

        stopwatch.Stop();
        _log.LogInformation(
            "Indexed {Chunks} chunks from {Files} files in {Seconds}s",
            indexed, files.Count, stopwatch.Elapsed.TotalSeconds);

        return new IngestResponse(files.Count, indexed, stopwatch.ElapsedMilliseconds);
    }
}
