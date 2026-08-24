using System.Collections.Concurrent;
using LegacyLens.Api.Embeddings;
using LegacyLens.Api.Storage;

namespace LegacyLens.Api.Ingestion;

/// <summary>What an indexing run is doing, or what it did.</summary>
public record IngestionJob(
    string Workspace,
    string RootPath,
    /// <summary>cloning, running, done, failed or cancelled.</summary>
    string State,
    int FilesTotal,
    int FilesDone,
    int ChunksIndexed,
    string? CurrentFile,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error)
{
    public bool Running => State is "running" or "cloning";

    /// <summary>
    /// Extrapolated from the files done so far, and null until there are any.
    ///
    /// The only number here that is not measured, which is why it is named for
    /// what it is rather than presented as a deadline.
    /// </summary>
    public long? EstimatedSecondsLeft
    {
        get
        {
            if (!Running || FilesDone == 0 || FilesTotal == 0) return null;

            var elapsed = (DateTimeOffset.UtcNow - StartedAt).TotalSeconds;
            return (long)(elapsed / FilesDone * (FilesTotal - FilesDone));
        }
    }
}

/// <summary>
/// Indexing, moved off the request.
///
/// Embedding runs at roughly two chunks a second on a CPU, so a real estate
/// takes hours. Held open as an HTTP request that is a connection timing out,
/// a browser tab nobody can close, and no way to know how far it got. The work
/// is the same work; what changes is that the reader can ask questions of the
/// fast half while it runs, and come back to this.
///
/// One run at a time, across all workspaces. A single embedding already
/// saturates every core, so a second concurrent run does not halve the wait,
/// it doubles both.
/// </summary>
public class IngestionJobs
{
    private readonly ConcurrentDictionary<string, IngestionJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations =
        new(StringComparer.Ordinal);

    /// <summary>A repository to fetch before there is anything to index.</summary>
    public record CloneSpec(string Url, string? Token, string CloneRoot);

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly SourceWalker _walker;
    private readonly CodeChunker _chunker;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<IngestionJobs> _log;

    public IngestionJobs(
        IServiceScopeFactory scopes,
        IConfiguration config,
        SourceWalker walker,
        CodeChunker chunker,
        ILoggerFactory loggers,
        ILogger<IngestionJobs> log)
    {
        _scopes = scopes;
        _config = config;
        _walker = walker;
        _chunker = chunker;
        _loggers = loggers;
        _log = log;
    }

    public IngestionJob? Status(string workspace) =>
        _jobs.TryGetValue(workspace, out var job) ? job : null;

    public IReadOnlyList<IngestionJob> All() => _jobs.Values.ToList();

    /// <summary>Whether any workspace is currently indexing.</summary>
    public IngestionJob? Busy() => _jobs.Values.FirstOrDefault(j => j.Running);

    /// <summary>
    /// Starts a run, or returns null if one is already going.
    ///
    /// Null rather than an exception: two clicks on the same button is a
    /// perfectly ordinary thing for a person to do, and it is not an error.
    /// </summary>
    public IngestionJob? Start(string workspace, string rootPath) =>
        Begin(workspace, rootPath, clone: null);

    /// <summary>
    /// Fetches a repository, then indexes it.
    ///
    /// One job rather than two, because from the reader's side it is one wait:
    /// they gave a URL and they are waiting for questions to become answerable.
    /// </summary>
    public IngestionJob? StartFromRepository(string workspace, CloneSpec clone) =>
        Begin(workspace, rootPath: string.Empty, clone);

    private IngestionJob? Begin(string workspace, string rootPath, CloneSpec? clone)
    {
        if (Busy() is not null) return null;

        var job = new IngestionJob(
            workspace, rootPath, clone is null ? "running" : "cloning",
            0, 0, 0, null, DateTimeOffset.UtcNow, null, null);

        _jobs[workspace] = job;

        var cancellation = new CancellationTokenSource();
        _cancellations[workspace] = cancellation;

        // Deliberately not awaited: the point is to answer the request now.
        _ = Task.Run(() => RunAsync(workspace, rootPath, clone, cancellation.Token));

        return job;
    }

    public bool Cancel(string workspace)
    {
        if (Status(workspace) is not { Running: true }) return false;
        if (!_cancellations.TryGetValue(workspace, out var cancellation)) return false;

        cancellation.Cancel();
        return true;
    }

    private async Task RunAsync(
        string workspace, string rootPath, CloneSpec? clone, CancellationToken ct)
    {
        // Its own connection, not the one serving requests. SQLite is in WAL
        // mode, so this writer and the readers answering questions during the
        // run do not wait for each other.
        using var store = new SqliteVectorStore(_config);
        using var scope = _scopes.CreateScope();

        var service = new IngestionService(
            _walker,
            _chunker,
            scope.ServiceProvider.GetRequiredService<IEmbeddingClient>(),
            store,
            _loggers.CreateLogger<IngestionService>());

        var progress = new Inline<IngestionProgress>(update => Update(workspace, job => job with
        {
            FilesTotal = update.FilesTotal,
            FilesDone = update.FilesDone,
            ChunksIndexed = update.ChunksIndexed,
            CurrentFile = update.CurrentFile,
        }));

        try
        {
            if (clone is not null)
            {
                var into = Path.Combine(
                    clone.CloneRoot, Cloning.FolderFor(clone.Url, workspace));

                var cloned = await new Cloning(_loggers.CreateLogger<Cloning>())
                    .CloneAsync(clone.Url, into, clone.Token, ct);

                if (!cloned.Ok)
                {
                    Update(workspace, job => job with
                    {
                        State = "failed",
                        FinishedAt = DateTimeOffset.UtcNow,
                        Error = cloned.Error,
                    });
                    return;
                }

                rootPath = cloned.Path!;

                // Written down before indexing starts, so re-indexing later
                // finds the clone rather than fetching the repository again.
                new Workspaces(store.Connection).SetRootPath(workspace, rootPath);

                Update(workspace, job => job with { State = "running", RootPath = rootPath });
            }

            var result = await service.IngestAsync(rootPath, workspace, progress, ct);

            Update(workspace, job => job with
            {
                State = "done",
                ChunksIndexed = result.ChunksIndexed,
                CurrentFile = null,
                FinishedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (OperationCanceledException)
        {
            // Everything embedded before the cancellation is already stored and
            // recorded, so this is a pause rather than a loss. Starting again
            // picks up from the file it stopped on.
            Update(workspace, job => job with
            {
                State = "cancelled", CurrentFile = null, FinishedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception failure)
        {
            _log.LogError(failure, "Indexing {Workspace} failed", workspace);

            Update(workspace, job => job with
            {
                State = "failed",
                CurrentFile = null,
                FinishedAt = DateTimeOffset.UtcNow,
                Error = failure is HttpRequestException
                    ? "Could not reach the embedding model. Is Ollama running?"
                    : failure.Message,
            });
        }
        finally
        {
            if (_cancellations.TryRemove(workspace, out var cancellation)) cancellation.Dispose();
        }
    }

    /// <summary>
    /// Applies a change to a job, dropping it if the job is gone.
    ///
    /// Deleting a workspace mid-run removes its job, and a progress report
    /// that arrived a moment later would otherwise put it back with no
    /// workspace behind it.
    /// </summary>
    private void Update(string workspace, Func<IngestionJob, IngestionJob> change)
    {
        if (!_jobs.TryGetValue(workspace, out var existing)) return;
        _jobs.TryUpdate(workspace, change(existing), existing);
    }

    /// <summary>
    /// Reports on the thread that called, rather than on the pool.
    ///
    /// Progress&lt;T&gt; posts its callbacks, so the last report of a run can land
    /// after the run has been marked done and overwrite the finished counts
    /// with older ones. What the reader sees then is a job that says it
    /// finished and indexed nothing.
    /// </summary>
    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Forgets a workspace's run, for when the workspace itself goes.</summary>
    public void Forget(string workspace)
    {
        Cancel(workspace);
        _jobs.TryRemove(workspace, out _);
    }
}
