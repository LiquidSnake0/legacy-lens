using System.Text;

namespace LegacyLens.Api.Generation;

/// <summary>
/// Turns retrieved chunks plus a question into the prompt sent to the model.
/// This class is where "it makes things up" is either solved or not.
/// </summary>
public class PromptBuilder
{
    /// <summary>
    /// Character budget for the whole prompt. A rough stand-in for the model's
    /// token limit, roughly four characters per token for source code.
    /// </summary>
    public int MaxChars { get; }

    public PromptBuilder(int maxChars = 12_000) => MaxChars = maxChars;

    /// <summary>
    /// Builds the prompt. <paramref name="hits"/> arrives ordered by score,
    /// best first, keep it that way when trimming to fit the budget, so what
    /// gets dropped is always the weakest evidence.
    /// </summary>
    private const string Header = """
        You are answering questions about a specific codebase. Excerpts from it
        are given below, each labelled with the file it came from and the exact
        lines it occupies.

        Three rules:

        1. Answer only from the excerpts below. They are the entire evidence
           available to you.
        2. If the excerpts do not contain the answer, say plainly that you do
           not know and stop. Do not reason from what the code is likely to do.
        3. Cite the file and line range supporting every claim you make.

        EXCERPTS
        ========

        """;

    private const string Reminder = """

        Answer only from the excerpts above, cite file and lines for each claim,
        and say you do not know if the answer is not there.
        """;

    private const string TruncationMarker = "\n[… excerpt truncated]\n\n";

    public string Build(string question, IReadOnlyList<SearchHit> hits)
    {
        // The question goes after the evidence and before the reminder, so the
        // instruction occupies both strong positions in the context. Models
        // attend worst to the middle of a long prompt, and the middle is
        // exactly where the excerpts are.
        var tail = $"\nQUESTION\n========\n\n{question}\n{Reminder}";

        // Reserve the ending before spending anything on excerpts. Overflowing
        // by exactly the question and the rules would be the worst possible
        // thing to lose.
        var budget = Math.Max(0, MaxChars - Header.Length - tail.Length);

        var body = new StringBuilder();

        foreach (var hit in hits)
        {
            // Without the label the model cannot cite this excerpt, and any
            // reference it produces for it is invented.
            var label = $"[{hit.Chunk.FilePath} lines {hit.Chunk.StartLine}-{hit.Chunk.EndLine}]\n";
            var block = label + hit.Chunk.Content + "\n\n";
            var remaining = budget - body.Length;

            if (block.Length <= remaining)
            {
                body.Append(block);
                continue;
            }

            // Hits arrive best-first, so stopping here drops the weakest
            // evidence, never the strongest.
            //
            // Except when nothing has been added yet: the single best excerpt
            // is worth more truncated than absent.
            if (body.Length == 0 && remaining > label.Length + TruncationMarker.Length + 200)
            {
                var room = remaining - label.Length - TruncationMarker.Length;
                body.Append(label)
                    .Append(hit.Chunk.Content.AsSpan(0, room))
                    .Append(TruncationMarker);
            }

            break;
        }

        return Header + body + tail;
    }
}
