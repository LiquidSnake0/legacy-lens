namespace LegacyLens.Api.Generation;

public class AnswerService
{
    private readonly Retriever _retriever;
    private readonly PromptBuilder _prompts;
    private readonly IChatClient _chat;

    public AnswerService(Retriever retriever, PromptBuilder prompts, IChatClient chat)
    {
        _retriever = retriever;
        _prompts = prompts;
        _chat = chat;
    }

    public async Task<AskResponse> AnswerAsync(AskRequest request, CancellationToken ct = default)
    {
        var hits = await _retriever.RetrieveAsync(request.Question, request.TopK, ct);

        // Nothing cleared the score floor. Say so rather than handing the model
        // an empty context and letting it fill the silence.
        if (hits.Count == 0)
        {
            return new AskResponse(
                "Nothing in the indexed code appears to relate to that question. " +
                "Either it is not covered by this repository, or the wording is too " +
                "far from how the code names things — try the terms the code itself uses.",
                []);
        }

        var answer = await _chat.CompleteAsync(_prompts.Build(request.Question, hits), ct);

        var sources = hits
            .Select(h => new Citation(h.Chunk.FilePath, h.Chunk.StartLine, h.Chunk.EndLine, h.Score))
            .ToList();

        return new AskResponse(answer, sources);
    }
}
