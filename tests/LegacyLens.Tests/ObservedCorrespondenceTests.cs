using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Correspondences read off finished migrations, kept apart from the ones
/// written from knowledge.
///
/// The rule that a correspondence is a judgement somebody signs exists because
/// a *guessed* one is dangerous: `System.Web.HttpContext` and its modern
/// namesake share a word and nothing else. These were not guessed. Four teams
/// took the old type and wrote one of the same name inside the successor, and
/// the framework was asked whether the successor really contains it. That is a
/// measurement, and this codebase trusts measurements over judgements
/// everywhere else.
///
/// They are still kept apart, because "somebody believes this" and "four teams
/// did this" are different claims and a reader is entitled to know which one
/// they are reading.
/// </summary>
public class ObservedCorrespondenceTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"catalogue-{Guid.NewGuid():N}.json");

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

    [Fact]
    public void An_observed_name_kept_its_name_so_it_maps_to_itself()
    {
        var catalogue = Written("""
            {
              "Old.Package": [
                {
                  "package": "New.Package",
                  "types": { "Written": "Rewritten" },
                  "observed": { "Measured": "2 migration(s): A, B" }
                }
              ]
            }
            """);

        var successor = catalogue.For("Old.Package").Single();

        Assert.Equal("Rewritten", successor.Types["Written"]);
        Assert.Equal("Measured", successor.Types["Measured"]);
    }

    [Fact]
    public void And_a_reader_can_tell_which_is_which()
    {
        var successor = Written("""
            {
              "Old.Package": [
                {
                  "package": "New.Package",
                  "types": { "Written": "Rewritten" },
                  "observed": { "Measured": "2 migration(s): A, B" }
                }
              ]
            }
            """).For("Old.Package").Single();

        Assert.Contains("Measured", successor.Observed.Keys);
        Assert.DoesNotContain("Written", successor.Observed.Keys);
        Assert.Equal("2 migration(s): A, B", successor.Observed["Measured"]);
    }

    [Fact]
    public void What_somebody_wrote_on_purpose_wins()
    {
        // A written entry is somebody having looked at that one case. An
        // observation is a pattern across migrations. Where they disagree the
        // person who looked wins, and quietly overwriting them with a
        // measurement would be the tool deciding it knows better.
        var successor = Written("""
            {
              "Old.Package": [
                {
                  "package": "New.Package",
                  "types": { "Both": "SomethingElse" },
                  "observed": { "Both": "1 migration(s): A" }
                }
              ]
            }
            """).For("Old.Package").Single();

        Assert.Equal("SomethingElse", successor.Types["Both"]);
    }

    [Fact]
    public void A_catalogue_with_no_observations_still_reads()
    {
        var successor = Written("""
            {
              "Old.Package": [ { "package": "New.Package", "types": { "A": "B" } } ]
            }
            """).For("Old.Package").Single();

        Assert.Empty(successor.Observed);
        Assert.Equal("B", successor.Types["A"]);
    }

    [Fact]
    public void The_shipped_catalogue_carries_what_the_migrations_taught_it()
    {
        // Read off the file that ships rather than a fixture, so that losing
        // the block is a failing test and not a quiet regression to a smaller
        // catalogue.
        var successor = Successors.Load()
            .For("Microsoft.AspNet.Mvc")
            .Single(s => s.Package == "Microsoft.AspNetCore.Mvc");

        Assert.Contains("TagBuilder", successor.Observed.Keys);
        Assert.Contains("ViewContext", successor.Observed.Keys);

        // Every observation says where it came from. An entry with no
        // provenance is indistinguishable from one somebody typed.
        foreach (var (name, where) in successor.Observed)
        {
            if (name.StartsWith("//", StringComparison.Ordinal)) continue;

            Assert.Contains("migration", where);
            Assert.Equal(name, successor.Types[name]);
        }
    }
}
