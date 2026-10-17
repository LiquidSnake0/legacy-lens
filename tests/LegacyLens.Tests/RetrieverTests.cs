using LegacyLens.Api;
using LegacyLens.Api.Generation;

namespace LegacyLens.Tests;

public class RetrieverTests
{
    private static SearchHit Hit(string path, int startLine, float score) =>
        new(new Chunk($"{path}#{startLine}", path, startLine, startLine + 20, "..."), score);

    [Fact]
    public void Best_matches_come_first()
    {
        // All three sit clearly above MinimumScore, so this exercises ordering
        // and nothing else.
        var ranked = Retriever.Rank(
            [Hit("a.cs", 1, 0.65f), Hit("b.cs", 1, 0.91f), Hit("c.cs", 1, 0.72f)], 3);

        Assert.Equal(["b.cs", "c.cs", "a.cs"], ranked.Select(h => h.Chunk.FilePath));
    }

    [Fact]
    public void Returns_at_most_topK()
    {
        var candidates = Enumerable.Range(1, 40).Select(i => Hit($"f{i}.cs", 1, 0.9f)).ToList();
        Assert.Equal(5, Retriever.Rank(candidates, 5).Count);
    }

    [Fact]
    public void Noise_below_the_score_floor_is_dropped()
    {
        var ranked = Retriever.Rank(
            [Hit("good.cs", 1, 0.80f), Hit("noise.cs", 1, 0.12f)], 6);

        Assert.Single(ranked);
        Assert.Equal("good.cs", ranked[0].Chunk.FilePath);
    }

    [Fact]
    public void Everything_below_the_floor_returns_nothing()
    {
        // Feeds the "I could not find that" path rather than handing the model
        // six irrelevant excerpts and inviting it to improvise.
        Assert.Empty(Retriever.Rank([Hit("a.cs", 1, 0.1f), Hit("b.cs", 1, 0.2f)], 6));
    }

    [Fact]
    public void A_hit_well_below_the_best_one_is_dropped_even_above_the_absolute_floor()
    {
        // 0.60 clears the absolute floor, but next to a 0.95 it is the vaguely
        // related excerpt that dilutes a strong answer.
        var ranked = Retriever.Rank([Hit("answer.cs", 1, 0.95f), Hit("vague.cs", 1, 0.60f)], 6);

        Assert.Single(ranked);
        Assert.Equal("answer.cs", ranked[0].Chunk.FilePath);
    }

    [Fact]
    public void The_relative_floor_only_bites_when_the_best_hit_is_clearly_ahead()
    {
        // Both are weak, both above the absolute floor, neither far from the
        // other: the relative cut sits below 0.52 and keeps both.
        var ranked = Retriever.Rank([Hit("a.cs", 1, 0.62f), Hit("b.cs", 1, 0.55f)], 6);
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void The_relative_floor_spares_hits_the_text_search_found()
    {
        // Agreement between the two searches, or an exact identifier match,
        // is a different claim than a middling cosine score.
        var ranked = Retriever.Rank(
            [Hit("answer.cs", 1, 0.95f),
             new SearchHit(Hit("both.cs", 1, 0.60f).Chunk, 0.60f, MatchSource.Both),
             new SearchHit(Hit("text.cs", 1, 0f).Chunk, 0f, MatchSource.Text)], 6);

        Assert.Equal(["answer.cs", "both.cs", "text.cs"], ranked.Select(h => h.Chunk.FilePath));
    }

    [Fact]
    public void The_relative_floor_can_be_turned_off()
    {
        var ranked = Retriever.Rank([Hit("answer.cs", 1, 0.95f), Hit("vague.cs", 1, 0.60f)], 6, relativeFloor: 0f);
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void No_single_file_may_fill_the_whole_context()
    {
        // A 4000-line file will match a query in many places. Without a cap it
        // crowds out the file that actually answers the question.
        var candidates = Enumerable.Range(1, 20)
            .Select(i => Hit("Huge.cs", i * 50, 0.95f))
            .Append(Hit("Answer.cs", 1, 0.80f))
            .ToList();

        var ranked = Retriever.Rank(candidates, 6);

        Assert.Equal(Retriever.MaxChunksPerFile, ranked.Count(h => h.Chunk.FilePath == "Huge.cs"));
        Assert.Contains(ranked, h => h.Chunk.FilePath == "Answer.cs");
    }

    [Fact]
    public void The_capped_file_keeps_its_best_chunks()
    {
        // Capping must not become "keep whichever three came out of the database
        // first".
        // Every score is above both floors, the absolute one and 0.7 of the
        // best, so line 100 can only be dropped by the per-file cap, which is
        // what this test is about.
        var candidates = new[]
        {
            Hit("Huge.cs", 100, 0.76f),
            Hit("Huge.cs", 200, 0.93f),
            Hit("Huge.cs", 300, 0.82f),
            Hit("Huge.cs", 400, 0.88f),
        };

        var kept = Retriever.Rank(candidates, 6)
            .Where(h => h.Chunk.FilePath == "Huge.cs")
            .Select(h => h.Chunk.StartLine)
            .ToList();

        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(100, kept);
    }

    [Fact]
    public void Empty_input_is_not_an_error()
    {
        Assert.Empty(Retriever.Rank([], 6));
    }
}
