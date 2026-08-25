using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The answer the catalogue was missing, and the largest one on real code.
///
/// It could say two things: this becomes that, and nothing does its job. Both
/// assume the problem still exists. The third is that **the framework withdrew
/// the feature**, so there is nothing to replace because there is nothing left
/// to do.
///
/// `AllowHtml` and `ValidateInput` turn off ASP.NET's request validation, and
/// ASP.NET Core has none. On nopCommerce 3.90 those two are 596 of the 857
/// calls the tool reported as types modern .NET does not have: seventy per cent
/// of that pile, answered by one sentence.
/// </summary>
public class WithdrawnFeatureTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"withdrawn-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private SuccessorCatalogue Written(string json)
    {
        File.WriteAllText(_path, json);
        return Successors.Load(_path);
    }

    private static UsageSurface Using(params (string Name, int Uses)[] types) =>
        new("Old.Package", [], types.Select(t => new ApiUse(t.Name, t.Uses, 1)).ToList(), 1, [], []);

    private const string Catalogue = """
        {
          "Old.Package": [
            {
              "package": "New.Package",
              "types": { "Kept": "Renamed", "Orphan": null },
              "removed": { "Withdrawn": "The framework stopped doing this at all." }
            }
          ]
        }
        """;

    [Fact]
    public void A_withdrawn_type_is_neither_covered_nor_unknown_nor_a_blocker()
    {
        var coverage = new Successors()
            .Rank(Using(("Withdrawn", 500)), Written(Catalogue))
            .Single();

        var gone = Assert.Single(coverage.Gone);

        Assert.Equal("Withdrawn", gone.Name);
        Assert.Equal(500, coverage.UsesGone);
        Assert.Empty(coverage.Unknown);
        Assert.Empty(coverage.Unavailable);
        Assert.Empty(coverage.Covered);
    }

    [Fact]
    public void It_is_kept_apart_from_a_type_with_no_counterpart()
    {
        // "Nothing does its job" leaves somebody with a problem. "The feature
        // went away" does not. Folding them together prices a deletion as a
        // rewrite, which is the difference between an afternoon and a quarter.
        var coverage = new Successors()
            .Rank(Using(("Withdrawn", 1), ("Orphan", 1)), Written(Catalogue))
            .Single();

        Assert.Equal("Withdrawn", Assert.Single(coverage.Gone).Name);
        Assert.Equal("Orphan", Assert.Single(coverage.Unavailable).Name);
    }

    [Fact]
    public void And_it_is_asked_before_the_others()
    {
        // A name in both blocks is answered by the withdrawal, because the
        // other three all assume the problem is still there.
        var coverage = new Successors()
            .Rank(Using(("Kept", 1)), Written("""
                {
                  "Old.Package": [
                    {
                      "package": "New.Package",
                      "types": { "Kept": "Renamed" },
                      "removed": { "Kept": "It went away after all." }
                    }
                  ]
                }
                """))
            .Single();

        Assert.Single(coverage.Gone);
        Assert.Empty(coverage.Covered);
    }

    [Fact]
    public void Every_withdrawal_says_why_it_went()
    {
        // A deletion nobody agreed to is still a change to their code. Without
        // a reason beside it, this is the tool telling somebody to delete
        // something and refusing to say what for.
        var successor = Successors.Load()
            .For("Microsoft.AspNet.Mvc")
            .Single(s => s.Package == "Microsoft.AspNetCore.Mvc");

        foreach (var (name, why) in successor.Removed)
        {
            if (name.StartsWith("//", StringComparison.Ordinal)) continue;

            Assert.False(string.IsNullOrWhiteSpace(why), $"{name} says nothing");
            Assert.True(why.Length > 40, $"{name} does not explain itself");
        }
    }

    [Fact]
    public void The_shipped_catalogue_knows_request_validation_went_away()
    {
        var successor = Successors.Load()
            .For("Microsoft.AspNet.Mvc")
            .Single(s => s.Package == "Microsoft.AspNetCore.Mvc");

        Assert.Contains("AllowHtml", successor.Removed.Keys);
        Assert.Contains("ValidateInput", successor.Removed.Keys);

        // And they are not also claimed as replaced, which would be two answers
        // to one question.
        Assert.DoesNotContain("AllowHtml", successor.Types.Keys);
    }

    [Fact]
    public void A_catalogue_with_no_withdrawals_still_reads()
    {
        var coverage = new Successors()
            .Rank(Using(("Kept", 1)), Written("""
                { "Old.Package": [ { "package": "New.Package", "types": { "Kept": "Renamed" } } ] }
                """))
            .Single();

        Assert.Empty(coverage.Gone);
        Assert.Equal(0, coverage.UsesGone);
        Assert.Single(coverage.Covered);
    }
}
