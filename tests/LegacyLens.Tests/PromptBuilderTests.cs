using LegacyLens.Api;
using LegacyLens.Api.Generation;

namespace LegacyLens.Tests;

/// <summary>
/// These check what can be checked deterministically: that the evidence reaches
/// the prompt intact and that the budget holds. Whether the model then behaves
/// is measured by using it — see docs/TODO.md #3.
/// </summary>
public class PromptBuilderTests
{
    private static SearchHit Hit(string path, int start, int end, string body, float score) =>
        new(new Chunk($"{path}#{start}", path, start, end, body), score);

    private static readonly SearchHit[] Hits =
    [
        Hit("Billing/PriceEngine.cs", 84, 131, "public decimal Compute(...)", 0.81f),
        Hit("Startup.cs", 40, 52, "services.AddSingleton<IRates>(...)", 0.69f),
    ];

    [Fact]
    public void The_question_reaches_the_prompt()
    {
        var prompt = new PromptBuilder().Build("Where is pricing calculated?", Hits);
        Assert.Contains("Where is pricing calculated?", prompt);
    }

    [Fact]
    public void Every_excerpt_reaches_the_prompt()
    {
        var prompt = new PromptBuilder().Build("Where is pricing calculated?", Hits);
        foreach (var hit in Hits)
            Assert.Contains(hit.Chunk.Content, prompt);
    }

    [Fact]
    public void Every_excerpt_is_labelled_with_its_location()
    {
        // The model cannot cite what it was not told. If the path and lines are
        // missing from the prompt, any citation in the answer is invented.
        var prompt = new PromptBuilder().Build("Where is pricing calculated?", Hits);

        Assert.Contains("Billing/PriceEngine.cs", prompt);
        Assert.Contains("84", prompt);
        Assert.Contains("131", prompt);
        Assert.Contains("Startup.cs", prompt);
    }

    [Fact]
    public void The_model_is_told_to_answer_only_from_the_excerpts()
    {
        var prompt = new PromptBuilder().Build("Where is pricing calculated?", Hits).ToLowerInvariant();
        Assert.Contains("only", prompt);
    }

    [Fact]
    public void The_model_is_told_it_may_say_it_does_not_know()
    {
        // Without an explicit escape hatch, a model asked a question it cannot
        // answer will answer anyway.
        var prompt = new PromptBuilder().Build("Where is pricing calculated?", Hits).ToLowerInvariant();
        Assert.True(
            prompt.Contains("don't know") || prompt.Contains("do not know") || prompt.Contains("not know"),
            "The prompt must permit an explicit 'I don't know'.");
    }

    [Fact]
    public void The_budget_is_respected()
    {
        var many = Enumerable.Range(1, 50)
            .Select(i => Hit($"File{i}.cs", 1, 40, new string('x', 1_000), 0.9f - i * 0.001f))
            .ToArray();

        var prompt = new PromptBuilder(maxChars: 5_000).Build("anything?", many);
        Assert.True(prompt.Length <= 6_000, $"Prompt of {prompt.Length} chars against a 5000 budget.");
    }

    [Fact]
    public void When_trimming_the_weakest_evidence_goes_first()
    {
        var strong = Hit("Strong.cs", 1, 40, new string('a', 2_000), 0.95f);
        var weak   = Hit("Weak.cs",   1, 40, new string('b', 2_000), 0.45f);

        var prompt = new PromptBuilder(maxChars: 2_500).Build("anything?", [strong, weak]);

        Assert.Contains("Strong.cs", prompt);
        Assert.DoesNotContain("Weak.cs", prompt);
    }

    [Fact]
    public void No_excerpts_still_produces_a_usable_prompt()
    {
        var prompt = new PromptBuilder().Build("anything?", []);
        Assert.False(string.IsNullOrWhiteSpace(prompt));
    }
}
