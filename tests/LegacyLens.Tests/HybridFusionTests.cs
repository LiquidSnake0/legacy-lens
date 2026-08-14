using LegacyLens.Api;
using LegacyLens.Api.Generation;

namespace LegacyLens.Tests;

public class HybridFusionTests
{
    private static SearchHit Hit(string id, float score = 0.6f) =>
        new(new Chunk(id, $"{id}.cs", 1, 20, "..."), score);

    private static IReadOnlyList<SearchHit> List(params string[] ids) =>
        ids.Select(id => Hit(id)).ToList();

    // ---- fusion ---------------------------------------------------------

    [Fact]
    public void A_chunk_both_searches_found_outranks_one_either_found_alone()
    {
        // The premise of fusion: agreement between two independent rankings is
        // stronger evidence than a good position in one.
        var fused = HybridFusion.Fuse(
            vector: List("only-vector", "agreed"),
            text: List("only-text", "agreed"));

        Assert.Equal("agreed", fused[0].Chunk.Id);
    }

    [Fact]
    public void A_chunk_only_the_text_search_found_still_appears()
    {
        // The whole reason the feature exists: an exact identifier the
        // embedding did not favour.
        var fused = HybridFusion.Fuse(vector: List("a", "b"), text: List("PriceEngine"));
        Assert.Contains(fused, h => h.Chunk.Id == "PriceEngine");
    }

    [Fact]
    public void Ranks_are_recorded_so_the_provenance_survives()
    {
        var fused = HybridFusion.Fuse(vector: List("x", "shared"), text: List("shared"));
        var shared = fused.Single(h => h.Chunk.Id == "shared");

        Assert.Equal(2, shared.VectorRank);
        Assert.Equal(1, shared.TextRank);
    }

    [Fact]
    public void A_text_only_hit_carries_no_vector_score()
    {
        // There is none to carry. Filling in a plausible number would be the
        // exact failure this project is built to avoid.
        var fused = HybridFusion.Fuse(vector: [], text: List("found-by-text"));
        Assert.Null(fused.Single().VectorScore);
    }

    [Fact]
    public void Position_decides_the_contribution_not_the_raw_score()
    {
        // Cosine sits in [-1,1], BM25 is unbounded and corpus-dependent. Fusing
        // on rank avoids having to reconcile them at all.
        var strong = HybridFusion.Fuse(vector: [Hit("a", 0.99f)], text: []);
        var weak = HybridFusion.Fuse(vector: [Hit("a", 0.53f)], text: []);

        Assert.Equal(strong[0].FusedScore, weak[0].FusedScore);
    }

    [Fact]
    public void Either_list_may_be_empty()
    {
        Assert.Single(HybridFusion.Fuse(vector: List("a"), text: []));
        Assert.Single(HybridFusion.Fuse(vector: [], text: List("a")));
        Assert.Empty(HybridFusion.Fuse(vector: [], text: []));
    }

    // ---- what the retriever does with it --------------------------------

    [Fact]
    public void A_chunk_found_by_both_is_marked_as_such()
    {
        var merged = Retriever.Merge(List("shared"), List("shared"));
        Assert.Equal(MatchSource.Both, merged.Single().Source);
    }

    [Fact]
    public void A_text_only_hit_ranked_far_down_is_dropped()
    {
        // BM25 returns something for almost any term. Position stands in for
        // the score floor these chunks cannot be measured against.
        var text = Enumerable.Range(1, 10).Select(i => Hit($"t{i}")).ToList();
        var merged = Retriever.Merge([], text);

        Assert.Equal(Retriever.TrustedTextRank, merged.Count);
        Assert.All(merged, h => Assert.Equal(MatchSource.Text, h.Source));
    }

    [Fact]
    public void A_weak_cosine_score_survives_when_the_text_search_agrees()
    {
        // Below the floor on its own, kept because both searches found it.
        var weak = new List<SearchHit> { Hit("exact-identifier", 0.30f) };
        var merged = Retriever.Merge(weak, List("exact-identifier"));

        Assert.Equal(MatchSource.Both, merged.Single().Source);
    }

    [Fact]
    public void The_score_floor_still_applies_to_vector_only_hits()
    {
        var merged = Retriever.Merge([Hit("noise", 0.20f)], []);
        Assert.Empty(Retriever.Rank(merged, 6));
    }

    [Fact]
    public void A_text_only_hit_is_not_measured_against_the_score_floor()
    {
        // It has no cosine score, and requiring one would discard it for the
        // very reason it was retrieved.
        var merged = Retriever.Merge([], List("PriceEngine"));
        Assert.Single(Retriever.Rank(merged, 6));
    }
}
