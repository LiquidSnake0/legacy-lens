using LegacyLens.Api.Embeddings;
using LegacyLens.Api.Ingestion;
using LegacyLens.Api.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LegacyLens.Tests;

/// <summary>
/// Indexing off the request.
///
/// The behaviour worth pinning is not that it indexes, which the service tests
/// already cover, but what the reader sees while it does: progress that moves,
/// a refusal when a second run is asked for, and a failure that arrives as a
/// state rather than as an unhandled exception on a thread nobody is watching.
/// </summary>
public class IngestionJobsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-jobs-{Guid.NewGuid():N}");

    private readonly string _repo;
    private readonly string _db;

    public IngestionJobsTests()
    {
        _repo = Path.Combine(_root, "repo");
        _db = Path.Combine(_root, "index.db");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void WriteSource(string name) =>
        File.WriteAllText(Path.Combine(_repo, name), $$"""
            namespace Sample;

            public class {{Path.GetFileNameWithoutExtension(name)}}
            {
                public int Value => 1;
            }
            """);

    private IngestionJobs Jobs(IEmbeddingClient? embeddings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["INDEX_PATH"] = _db })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(embeddings ?? new StubEmbeddings());
        var provider = services.BuildServiceProvider();

        return new IngestionJobs(
            provider.GetRequiredService<IServiceScopeFactory>(),
            config,
            new SourceWalker(),
            new CodeChunker(),
            NullLoggerFactory.Instance,
            NullLogger<IngestionJobs>.Instance);
    }

    private static async Task<IngestionJob> Settled(IngestionJobs jobs, string workspace)
    {
        // Polled rather than awaited, because polling is exactly what the
        // interface does and the job deliberately hands back no task.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = jobs.Status(workspace);
            if (job is not null && !job.Running) return job;
            await Task.Delay(25);
        }

        throw new TimeoutException($"The run never settled: {jobs.Status(workspace)?.State}");
    }

    [Fact]
    public async Task A_run_finishes_and_says_what_it_indexed()
    {
        WriteSource("A.cs");
        WriteSource("B.cs");

        var jobs = Jobs();
        Assert.NotNull(jobs.Start("alpha", _repo));

        var job = await Settled(jobs, "alpha");

        Assert.Equal("done", job.State);
        Assert.True(job.ChunksIndexed > 0);
        Assert.Null(job.Error);
        Assert.NotNull(job.FinishedAt);
    }

    [Fact]
    public async Task Progress_is_counted_against_the_files_that_need_work()
    {
        WriteSource("A.cs");
        WriteSource("B.cs");

        var jobs = Jobs();
        jobs.Start("alpha", _repo);
        var first = await Settled(jobs, "alpha");

        Assert.Equal(2, first.FilesTotal);
        Assert.Equal(2, first.FilesDone);

        // Second run: nothing changed, so there is no work, and a bar counting
        // every file found would sit at 100% having done nothing.
        jobs.Start("alpha", _repo);
        var second = await Settled(jobs, "alpha");

        Assert.Equal("done", second.State);
        Assert.Equal(0, second.FilesTotal);
    }

    [Fact]
    public async Task A_second_run_is_refused_while_one_is_going()
    {
        WriteSource("A.cs");

        var jobs = Jobs(new SlowEmbeddings(TimeSpan.FromMilliseconds(400)));

        Assert.NotNull(jobs.Start("alpha", _repo));

        // A single embedding already saturates every core, so a concurrent run
        // does not halve the wait, it doubles both.
        Assert.Null(jobs.Start("beta", _repo));

        await Settled(jobs, "alpha");

        // Once it is over, the next one is allowed.
        Assert.NotNull(jobs.Start("beta", _repo));
        await Settled(jobs, "beta");
    }

    [Fact]
    public async Task A_failure_arrives_as_a_state_rather_than_as_a_lost_exception()
    {
        var jobs = Jobs();

        jobs.Start("alpha", Path.Combine(_root, "no-such-directory"));
        var job = await Settled(jobs, "alpha");

        Assert.Equal("failed", job.State);
        Assert.NotNull(job.Error);
    }

    [Fact]
    public async Task An_unreachable_model_is_reported_in_words_the_reader_can_act_on()
    {
        WriteSource("A.cs");

        var jobs = Jobs(new UnreachableEmbeddings());

        jobs.Start("alpha", _repo);
        var job = await Settled(jobs, "alpha");

        Assert.Equal("failed", job.State);
        Assert.Contains("Ollama", job.Error);
    }

    [Fact]
    public async Task Cancelling_keeps_what_was_already_embedded()
    {
        for (var i = 0; i < 6; i++) WriteSource($"File{i}.cs");

        var jobs = Jobs(new SlowEmbeddings(TimeSpan.FromMilliseconds(120)));
        jobs.Start("alpha", _repo);

        // Long enough for at least one file to be stored and recorded.
        await Task.Delay(300);
        Assert.True(jobs.Cancel("alpha"));

        var job = await Settled(jobs, "alpha");
        Assert.Equal("cancelled", job.State);

        // Resuming does the rest rather than starting over.
        jobs.Start("alpha", _repo);
        var resumed = await Settled(jobs, "alpha");

        Assert.Equal("done", resumed.State);
        Assert.True(resumed.FilesTotal < 6, $"resumed run re-did {resumed.FilesTotal} files");
    }

    [Fact]
    public void Cancelling_something_that_is_not_running_is_refused_rather_than_ignored()
    {
        Assert.False(Jobs().Cancel("never-started"));
    }

    [Fact]
    public void A_run_that_has_not_started_has_nothing_to_report()
    {
        Assert.Null(Jobs().Status("never-started"));
    }

    [Fact]
    public void The_time_left_is_absent_until_there_is_something_to_extrapolate_from()
    {
        var fresh = new IngestionJob(
            "alpha", "/repo", "running", 10, 0, 0, null, DateTimeOffset.UtcNow, null, null);

        Assert.Null(fresh.EstimatedSecondsLeft);

        var underway = fresh with
        {
            FilesDone = 5, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
        };

        Assert.NotNull(underway.EstimatedSecondsLeft);
        Assert.InRange(underway.EstimatedSecondsLeft!.Value, 8, 12);
    }

    private class StubEmbeddings : IEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new[] { 1f, 0f });

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new[] { 1f, 0f }).ToList());
    }

    /// <summary>Slow enough that a second start lands while the first is going.</summary>
    private class SlowEmbeddings(TimeSpan delay) : IEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new[] { 1f, 0f });

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            return texts.Select(_ => new[] { 1f, 0f }).ToList();
        }
    }

    private class UnreachableEmbeddings : IEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            throw new HttpRequestException("connection refused");

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new HttpRequestException("connection refused");
    }
}
