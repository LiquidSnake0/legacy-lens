using System.Runtime.CompilerServices;

using LegacyLens.Api.Storage;

namespace LegacyLens.Api.Generation;

/// <summary>One piece of a streamed answer.</summary>
public abstract record AnswerEvent
{
    /// <summary>
    /// The citations, sent before a single token of the answer.
    ///
    /// They are known the moment retrieval finishes, and showing them first
    /// means the reader sees which files are about to be discussed while the
    /// model is still thinking. It also makes the wait legible rather than
    /// blank.
    /// </summary>
    public sealed record Sources(IReadOnlyList<Citation> Citations) : AnswerEvent;

    public sealed record Token(string Text) : AnswerEvent;

    public sealed record Failed(string Message) : AnswerEvent;

    public sealed record Done : AnswerEvent;
}

public class AnswerService
{
    private readonly Retriever _retriever;
    private readonly PromptBuilder _prompts;
    private readonly IChatClients _chats;

    public AnswerService(Retriever retriever, PromptBuilder prompts, IChatClients chats)
    {
        _retriever = retriever;
        _prompts = prompts;
        _chats = chats;
    }

    public async Task<AskResponse> AnswerAsync(AskRequest request,
        string workspace = Workspaces.Default, CancellationToken ct = default)
    {
        var hits = await _retriever.RetrieveAsync(request.Question, request.TopK, workspace, ct);

        // Nothing cleared the score floor. Say so rather than handing the model
        // an empty context and letting it fill the silence.
        if (hits.Count == 0)
        {
            return new AskResponse(NothingFound, []);
        }

        var answer = await _chats.For(request.Model)
            .CompleteAsync(_prompts.Build(request.Question, hits), ct);

        return new AskResponse(answer, Cite(hits));
    }

    /// <summary>
    /// The same answer, streamed. Citations first, then tokens as they come.
    /// </summary>
    public async IAsyncEnumerable<AnswerEvent> StreamAsync(
        AskRequest request,
        string workspace = Workspaces.Default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // C# forbids yield return inside a catch, so failures are captured
        // here and reported after the block.
        IReadOnlyList<SearchHit> hits = [];
        IChatClient? chat = null;
        string? failure = null;

        try
        {
            // Chosen before retrieving: a hosted model asked for without a key
            // should say so at once rather than after the search has run.
            chat = _chats.For(request.Model);
            hits = await _retriever.RetrieveAsync(request.Question, request.TopK, workspace, ct);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            failure = Explain(exception);
        }

        if (failure is not null)
        {
            yield return new AnswerEvent.Failed(failure);
            yield break;
        }

        if (hits.Count == 0)
        {
            yield return new AnswerEvent.Sources([]);
            yield return new AnswerEvent.Token(NothingFound);
            yield return new AnswerEvent.Done();
            yield break;
        }

        yield return new AnswerEvent.Sources(Cite(hits));

        // The enumerator is driven by hand rather than with await foreach: a
        // failure mid-stream has to become an event the caller can render, and
        // yield return is not allowed inside a catch.
        var tokens = chat!.StreamAsync(_prompts.Build(request.Question, hits), ct)
                          .GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                string? token = null;

                try
                {
                    if (await tokens.MoveNextAsync()) token = tokens.Current;
                }
                catch (Exception exception) when (exception is HttpRequestException
                                                            or InvalidOperationException)
                {
                    failure = Explain(exception);
                }

                if (failure is not null) break;
                if (token is null) break;

                yield return new AnswerEvent.Token(token);
            }
        }
        finally
        {
            await tokens.DisposeAsync();
        }

        yield return failure is not null
            ? new AnswerEvent.Failed(failure)
            : new AnswerEvent.Done();
    }

    /// <summary>
    /// The failure with what to do about it, when the model said why.
    ///
    /// A stream has one line to report a failure in, so the hint has to travel
    /// inside the message or it never arrives at all.
    /// </summary>
    private static string Explain(Exception exception) => exception is ModelRefused refused
        ? $"{refused.Message} {refused.Hint}"
        : exception.Message;

    private const string NothingFound =
        "Nothing in the indexed code appears to relate to that question. "
      + "Either it is not covered by this repository, or the wording is too "
      + "far from how the code names things, try the terms the code itself uses.";

    private static List<Citation> Cite(IReadOnlyList<SearchHit> hits) =>
        hits.Select(h => new Citation(
                h.Chunk.FilePath, h.Chunk.StartLine, h.Chunk.EndLine, h.Score,
                h.Source.ToString().ToLowerInvariant()))
            .ToList();
}
