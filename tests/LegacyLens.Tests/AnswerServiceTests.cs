using Microsoft.Data.Sqlite;

using LegacyLens.Api;
using LegacyLens.Api.Embeddings;
using LegacyLens.Api.Generation;
using LegacyLens.Api.Storage;

namespace LegacyLens.Tests;

/// <summary>
/// The streaming path, which has branches the non-streaming one does not: a
/// failure has to become an event rather than an exception, because by the time
/// it happens the response has already started and the status code is spent.
/// </summary>
public class AnswerServiceTests
{
    [Fact]
    public async Task Citations_arrive_before_any_of_the_answer()
    {
        // The whole reason for streaming. Retrieval takes two seconds and
        // generation takes forty, so holding the sources back until the end
        // wastes the only part of the wait that carries information.
        var events = await Collect(Service(
            found: [Hit("Billing/PriceEngine.cs", 0.81f)],
            tokens: ["Pricing ", "lives there."]));

        Assert.IsType<AnswerEvent.Sources>(events[0]);
        Assert.All(events.Skip(1).Take(2), e => Assert.IsType<AnswerEvent.Token>(e));
        Assert.IsType<AnswerEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task Tokens_are_passed_through_unaltered()
    {
        var events = await Collect(Service(
            found: [Hit("a.cs", 0.9f)],
            tokens: ["Pricing is computed ", "in PriceEngine."]));

        var text = string.Concat(events.OfType<AnswerEvent.Token>().Select(t => t.Text));
        Assert.Equal("Pricing is computed in PriceEngine.", text);
    }

    [Fact]
    public async Task Nothing_retrieved_says_so_instead_of_inventing()
    {
        // Rank() drops everything below the score floor, so an empty result is
        // the normal outcome for a question this codebase cannot answer.
        var events = await Collect(Service(found: [], tokens: ["never asked"]));

        Assert.Empty(Assert.IsType<AnswerEvent.Sources>(events[0]).Citations);
        Assert.Contains("Nothing in the indexed code",
            Assert.IsType<AnswerEvent.Token>(events[1]).Text);
        Assert.IsType<AnswerEvent.Done>(events[2]);
    }

    [Fact]
    public async Task A_model_that_is_down_fails_before_the_sources()
    {
        // Retrieval embeds the question, so an unreachable Ollama breaks here
        // first. No citations exist yet, and pretending otherwise would show an
        // empty source list next to an error.
        var events = await Collect(Service(
            found: [Hit("a.cs", 0.9f)],
            tokens: [],
            embeddingFails: new HttpRequestException("connection refused")));

        var failure = Assert.IsType<AnswerEvent.Failed>(Assert.Single(events));
        Assert.Contains("connection refused", failure.Message);
    }

    [Fact]
    public async Task A_model_that_dies_mid_answer_keeps_what_it_already_said()
    {
        // Discarding the half-written answer would punish the reader for the
        // model's failure. The tokens that did arrive are still worth showing.
        var events = await Collect(Service(
            found: [Hit("a.cs", 0.9f)],
            tokens: ["Pricing is "],
            streamFailsAfter: new InvalidOperationException("Ollama returned 500.")));

        Assert.Equal("Pricing is ",
            string.Concat(events.OfType<AnswerEvent.Token>().Select(t => t.Text)));

        Assert.Contains("Ollama returned 500.",
            Assert.IsType<AnswerEvent.Failed>(events[^1]).Message);

        // Failed and Done are alternatives, not a sequence: a client that hides
        // the spinner on Done would leave it spinning forever after a failure.
        Assert.DoesNotContain(events, e => e is AnswerEvent.Done);
    }

    [Fact]
    public async Task How_a_chunk_was_found_reaches_the_citation()
    {
        // A chunk found by text alone has no meaningful cosine score. The UI
        // needs to know that to avoid presenting a fused rank as a similarity.
        //
        // Returned by the text search only: a chunk both searches find is
        // marked Both, which is what the first version of this test measured
        // by accident.
        var hit = new SearchHit(
            new Chunk("id", "a.cs", 1, 20, "..."), 0.9f, MatchSource.Text);

        var events = await Collect(Service(found: [], text: [hit], tokens: ["ok"]));

        var citation = Assert.Single(Assert.IsType<AnswerEvent.Sources>(events[0]).Citations);
        Assert.Equal("text", citation.FoundBy);
    }

    private static SearchHit Hit(string path, float score) =>
        new(new Chunk(path, path, 1, 40, "..."), score, MatchSource.Both);

    private static AnswerService Service(
        IReadOnlyList<SearchHit> found,
        IReadOnlyList<string> tokens,
        IReadOnlyList<SearchHit>? text = null,
        Exception? embeddingFails = null,
        Exception? streamFailsAfter = null) =>
        new(new Retriever(new FakeEmbeddings(embeddingFails), new FakeStore(found, text ?? found)),
            new PromptBuilder(),
            new FakeChat(tokens, streamFailsAfter));

    private static async Task<List<AnswerEvent>> Collect(AnswerService service)
    {
        var events = new List<AnswerEvent>();
        await foreach (var item in service.StreamAsync(new AskRequest("where is pricing?")))
        {
            events.Add(item);
        }
        return events;
    }

    private class FakeEmbeddings(Exception? fails) : IEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            fails is not null ? Task.FromException<float[]>(fails) : Task.FromResult(new[] { 1f });

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Returns whatever it was handed, so the tests exercise AnswerService
    /// rather than re-testing ranking and fusion, which have their own tests.
    /// </summary>
    private class FakeStore(IReadOnlyList<SearchHit> vector, IReadOnlyList<SearchHit> text)
        : IVectorStore
    {
        public SqliteConnection Connection => throw new NotSupportedException();

        public Task<IReadOnlyList<SearchHit>> SearchAsync(
            float[] query, int topK, string workspace = Workspaces.Default,
            CancellationToken ct = default) => Task.FromResult(vector);

        public Task<IReadOnlyList<SearchHit>> SearchTextAsync(
            string query, int topK, string workspace = Workspaces.Default,
            CancellationToken ct = default) => Task.FromResult(text);

        public Task UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks,
            string workspace = Workspaces.Default, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Chunk?> ExcerptAsync(
            string filePath, int startLine, string workspace = Workspaces.Default,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ClearAsync(string workspace = Workspaces.Default, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(string workspace = Workspaces.Default,
            CancellationToken ct = default) => Task.FromResult(vector.Count);
    }

    private class FakeChat(IReadOnlyList<string> tokens, Exception? failsAfter) : IChatClient
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Concat(tokens));

        public async IAsyncEnumerable<string> StreamAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var token in tokens)
            {
                await Task.Yield();
                yield return token;
            }

            if (failsAfter is not null) throw failsAfter;
        }
    }
}
