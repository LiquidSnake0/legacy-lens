using LegacyLens.Api.Ingestion;

namespace LegacyLens.Tests;

/// <summary>
/// These assert invariants, not a strategy. How the file gets cut is a design
/// decision — but wherever the cuts land, the line numbers have to be true and
/// no code may be silently dropped, because a citation nobody can verify is
/// worse than no citation.
/// </summary>
public class CodeChunkerTests
{
    private const string SampleCode = """
        using System;

        namespace Billing
        {
            public class PriceEngine
            {
                public decimal Compute(Customer customer, int quantity)
                {
                    var rate = _rates[customer.Tier];
                    var gross = rate * quantity;
                    return ApplyDiscounts(customer, gross);
                }

                private decimal ApplyDiscounts(Customer customer, decimal gross)
                {
                    var afterVolume = _volume.Apply(gross);
                    return _contractual.Apply(customer, afterVolume);
                }
            }
        }
        """;

    [Fact]
    public void Empty_file_produces_no_chunks()
    {
        Assert.Empty(new CodeChunker().Split("Empty.cs", ""));
    }

    [Fact]
    public void Whitespace_only_file_produces_no_chunks()
    {
        Assert.Empty(new CodeChunker().Split("Blank.cs", "\n\n   \n\t\n"));
    }

    [Fact]
    public void Short_file_stays_in_one_chunk()
    {
        var chunks = new CodeChunker().Split("Small.cs", SampleCode);
        Assert.Single(chunks);
    }

    [Fact]
    public void Line_numbers_are_one_based()
    {
        var chunks = new CodeChunker().Split("Small.cs", SampleCode);
        Assert.Equal(1, chunks[0].StartLine);
    }

    [Fact]
    public void Cited_lines_actually_contain_the_cited_content()
    {
        // The invariant the whole tool rests on: open the file at the quoted
        // lines and you must find what the chunk claimed was there.
        var source = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"var line{i} = {i};"));
        var lines = source.Split('\n');

        foreach (var chunk in new CodeChunker(maxChars: 500).Split("Long.cs", source))
        {
            Assert.InRange(chunk.StartLine, 1, lines.Length);
            Assert.InRange(chunk.EndLine, chunk.StartLine, lines.Length);

            var expected = string.Join('\n', lines[(chunk.StartLine - 1)..chunk.EndLine]);
            Assert.Equal(expected.Trim(), chunk.Content.Trim());
        }
    }

    [Fact]
    public void Every_line_of_code_appears_in_at_least_one_chunk()
    {
        var source = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"var line{i} = {i};"));
        var chunks = new CodeChunker(maxChars: 500).Split("Long.cs", source);

        var covered = new HashSet<int>();
        foreach (var chunk in chunks)
            for (var line = chunk.StartLine; line <= chunk.EndLine; line++)
                covered.Add(line);

        var missing = Enumerable.Range(1, 400).Where(l => !covered.Contains(l)).ToList();
        Assert.True(missing.Count == 0, $"Lines never indexed: {string.Join(", ", missing.Take(10))}");
    }

    [Fact]
    public void Chunks_are_returned_in_file_order()
    {
        var source = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"var line{i} = {i};"));
        var chunks = new CodeChunker(maxChars: 500).Split("Long.cs", source);

        for (var i = 1; i < chunks.Count; i++)
            Assert.True(chunks[i].StartLine > chunks[i - 1].StartLine,
                "Chunks must advance through the file.");
    }

    [Fact]
    public void Chunks_stay_near_the_size_budget()
    {
        var source = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"var line{i} = {i};"));

        foreach (var chunk in new CodeChunker(maxChars: 500).Split("Long.cs", source))
            // Some slack: finishing the current line rather than cutting it in
            // half is correct. Doubling the budget is not.
            Assert.True(chunk.Content.Length <= 500 * 2,
                $"Chunk of {chunk.Content.Length} chars against a 500 budget.");
    }

    [Fact]
    public void A_single_line_longer_than_the_budget_is_not_dropped()
    {
        // Minified files and generated code hit this. Whatever the handling is,
        // it must not be silent data loss.
        var monster = new string('x', 5_000);
        var chunks = new CodeChunker(maxChars: 500).Split("Minified.js", $"var a = 1;\n{monster}\nvar b = 2;");
        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, c => c.EndLine >= 2);
    }

    [Fact]
    public void Ids_are_unique_within_a_file()
    {
        var source = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"var line{i} = {i};"));
        var chunks = new CodeChunker(maxChars: 500).Split("Long.cs", source);
        Assert.Equal(chunks.Count, chunks.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Ids_are_stable_across_runs()
    {
        // Re-indexing an unchanged file has to update rows, not duplicate them.
        var first  = new CodeChunker().Split("Small.cs", SampleCode).Select(c => c.Id);
        var second = new CodeChunker().Split("Small.cs", SampleCode).Select(c => c.Id);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Embedding_text_carries_the_file_path()
    {
        // A bare fragment of code embeds poorly. The path is most of the context
        // a short chunk has.
        var chunk = new CodeChunker().Split("Billing/PriceEngine.cs", SampleCode)[0];
        Assert.Contains("Billing/PriceEngine.cs", chunk.EmbeddingText);
        Assert.Contains("ApplyDiscounts", chunk.EmbeddingText);
    }
}
