using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// What replaces what, scored against what a codebase actually uses.
///
/// The distinction the whole thing turns on is between a type the catalogue
/// says has no replacement and a type the catalogue says nothing about.
/// Folding those together turns "we have not looked at this" into "this is
/// fine", which is the sentence that gets a migration signed off and then
/// discovered in month four.
/// </summary>
public class SuccessorsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-successors-{Guid.NewGuid():N}");

    public SuccessorsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Catalogue(string json)
    {
        var path = Path.Combine(_root, "successors.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static UsageSurface Surface(params (string Name, int Uses)[] types) =>
        new("Old.Package", ["Old.Namespace"],
            types.Select(t => new ApiUse(t.Name, t.Uses, 1)).ToList(), 1, []);

    /* ---- reading it ---- */

    [Fact]
    public void A_catalogue_is_read_from_a_file_rather_than_compiled_in()
    {
        var path = Catalogue("""
            {
              "Old.Package": [
                { "package": "New.Package", "note": "why", "types": { "A": "B" } }
              ]
            }
            """);

        var catalogue = Successors.Load(path);

        var candidate = Assert.Single(catalogue.For("Old.Package"));
        Assert.Equal("New.Package", candidate.Package);
        Assert.Equal("why", candidate.Note);
        Assert.Equal("B", candidate.Types["A"]);
    }

    [Fact]
    public void Commentary_in_the_file_is_ignored_rather_than_fatal()
    {
        // Found by running it, not by a test. This file is written and edited
        // by hand, so it carries the reasoning beside the data, and a strict
        // deserialise choked on the first comment key before reading anything.
        var path = Catalogue("""
            {
              "//": "why this file exists",
              "//1": "a second remark",
              "Old.Package": [
                { "package": "New.Package", "types": { "A": "B" } }
              ]
            }
            """);

        Assert.Single(Successors.Load(path).For("Old.Package"));
    }

    [Fact]
    public void A_missing_catalogue_says_where_it_looked_rather_than_throwing()
    {
        // The measurements work without a catalogue, and refusing to start over
        // one missing file would take them down too.
        var catalogue = Successors.Load(Path.Combine(_root, "nowhere.json"));

        Assert.Empty(catalogue.Packages);
        Assert.Contains("nowhere.json", catalogue.Source);
    }

    [Fact]
    public void A_broken_catalogue_says_why_rather_than_throwing()
    {
        var catalogue = Successors.Load(Catalogue("{ this is not json"));

        Assert.Empty(catalogue.Packages);
        Assert.Contains("could not be read", catalogue.Source);
    }

    [Fact]
    public void A_package_nobody_catalogued_scores_nothing_rather_than_zero_percent()
    {
        // Nothing to say and "covers 0%" are different answers, and only one of
        // them is true.
        var catalogue = Successors.Load(Catalogue("""{ "Other.Package": [] }"""));

        Assert.Empty(new Successors().Rank(Surface(("A", 1)), catalogue));
    }

    /* ---- scoring it ---- */

    private const string ThreeOutcomes = """
        {
          "Old.Package": [
            {
              "package": "New.Package",
              "note": "the trade",
              "types": { "Kept": "Renamed", "Gone": null }
            }
          ]
        }
        """;

    [Fact]
    public void A_type_with_a_named_replacement_is_covered()
    {
        var coverage = Assert.Single(new Successors()
            .Rank(Surface(("Kept", 3)), Successors.Load(Catalogue(ThreeOutcomes))));

        Assert.Equal(["Kept"], coverage.Covered.Select(t => t.Name));
        Assert.Equal(100, coverage.Percent);
        Assert.False(coverage.Blocked);
    }

    [Fact]
    public void A_type_the_catalogue_says_has_no_replacement_is_a_blocker()
    {
        var coverage = Assert.Single(new Successors()
            .Rank(Surface(("Gone", 2)), Successors.Load(Catalogue(ThreeOutcomes))));

        Assert.Equal(["Gone"], coverage.Unavailable.Select(t => t.Name));
        Assert.True(coverage.Blocked);
        Assert.Empty(coverage.Unknown);
    }

    [Fact]
    public void A_type_the_catalogue_says_nothing_about_is_unknown_and_not_fine()
    {
        // The distinction this whole file exists for. Null is a recorded fact;
        // absent is an admission.
        var coverage = Assert.Single(new Successors()
            .Rank(Surface(("Unheard", 4)), Successors.Load(Catalogue(ThreeOutcomes))));

        Assert.Equal(["Unheard"], coverage.Unknown.Select(t => t.Name));
        Assert.Empty(coverage.Unavailable);
        Assert.False(coverage.Blocked);
        Assert.Equal(0, coverage.Percent);
    }

    [Fact]
    public void Coverage_is_weighted_by_calls_rather_than_by_type()
    {
        // One type used five hundred times and one used once are not the same
        // amount of work, and counting them equally is how a coverage figure
        // becomes a lie.
        var coverage = Assert.Single(new Successors().Rank(
            Surface(("Kept", 90), ("Unheard", 10)),
            Successors.Load(Catalogue(ThreeOutcomes))));

        Assert.Equal(90, coverage.Percent);
        Assert.Equal(90, coverage.UsesCovered);
        Assert.Equal(10, coverage.UsesUnknown);
    }

    [Fact]
    public void The_candidate_covering_most_of_the_calls_comes_first()
    {
        var path = Catalogue("""
            {
              "Old.Package": [
                { "package": "Thin", "types": { "A": "a" } },
                { "package": "Thick", "types": { "A": "a", "B": "b" } }
              ]
            }
            """);

        var ranked = new Successors()
            .Rank(Surface(("A", 1), ("B", 5)), Successors.Load(path));

        Assert.Equal("Thick", ranked[0].Candidate);
        Assert.Equal("Thin", ranked[1].Candidate);
    }

    [Fact]
    public void A_replacement_that_is_removal_is_a_candidate_like_any_other()
    {
        // Microsoft.Web.Infrastructure has no successor because nothing needs
        // to succeed it: the reference is deleted. An empty package name says
        // that, and it is an answer rather than a gap.
        var path = Catalogue("""
            {
              "Old.Package": [
                { "package": "", "note": "delete the reference", "types": { "A": null } }
              ]
            }
            """);

        var coverage = Assert.Single(new Successors().Rank(Surface(("A", 1)), Successors.Load(path)));

        Assert.Equal(string.Empty, coverage.Candidate);
        Assert.Equal("delete the reference", coverage.Note);
    }

    /* ---- the shipped catalogue ---- */

    [Fact]
    public void The_catalogue_in_the_repository_loads_and_says_what_has_no_successor()
    {
        var catalogue = Successors.Load();

        // Skipped rather than failed when run from somewhere the file is not
        // beside: this asserts on the shipped data, not on the loader.
        if (catalogue.Packages.Count == 0) return;

        var mvc = Assert.Single(catalogue.For("Microsoft.AspNet.Mvc"));

        Assert.Equal("Microsoft.AspNetCore.Mvc", mvc.Package);
        Assert.Equal("IActionResult", mvc.Types["ActionResult"]);

        // The entries that matter most: recorded as having no counterpart
        // rather than left out and assumed fine.
        Assert.Null(mvc.Types["ChildActionOnly"]);
        Assert.Null(mvc.Types["AreaRegistration"]);
    }
}
