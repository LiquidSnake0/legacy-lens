namespace LegacyLens.Api.Generation;

/// <summary>
/// Turns retrieved chunks plus a question into the prompt sent to the model.
/// This class is where "it makes things up" is either solved or not.
/// </summary>
public class PromptBuilder
{
    /// <summary>
    /// Character budget for the whole prompt. A rough stand-in for the model's
    /// token limit — roughly four characters per token for source code.
    /// </summary>
    public int MaxChars { get; }

    public PromptBuilder(int maxChars = 12_000) => MaxChars = maxChars;

    /// <summary>
    /// Builds the prompt. <paramref name="hits"/> arrives ordered by score,
    /// best first — keep it that way when trimming to fit the budget, so what
    /// gets dropped is always the weakest evidence.
    /// </summary>
    public string Build(string question, IReadOnlyList<SearchHit> hits)
    {
        // ---------------------------------------------------------------
        // TODO — see docs/TODO.md #3.
        //
        // Tests in tests/LegacyLens.Tests/PromptBuilderTests.cs assert that
        // the prompt carries every excerpt's path and line range, that the
        // question survives, that the budget is respected, and that low-scoring
        // chunks are the ones dropped.
        //
        // They do not — and cannot — assert that the model behaves. That part
        // is measured by using it, which is why docs/TODO.md lists the
        // instructions worth including and why.
        // ---------------------------------------------------------------
        throw new NotImplementedException(
            "PromptBuilder.Build is not implemented yet — see docs/TODO.md #3.");
    }
}
