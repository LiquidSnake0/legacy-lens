using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Marking the tool against a migration that already happened.
///
/// Nobody can evaluate a migration tool, because there is no correct answer to
/// compare it against, which is why the field is sold on adjectives. A codebase
/// that exists in both states removes the problem: what the team decided is a
/// matter of record, and the tool said something before the fact.
///
/// The fixtures here are small on purpose. The measurement that matters is on a
/// real pair and lives in the roadmap; what these pin is that each of the four
/// possible fates is told apart from the others, and that the agreement rule
/// cannot quietly start counting things it was never asked about.
/// </summary>
public class HindsightTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(), $"lens-hindsight-{Guid.NewGuid():N}");

    private readonly string _before;
    private readonly string _after;

    public HindsightTests()
    {
        _before = Path.Combine(_work, "before");
        _after = Path.Combine(_work, "after");
        Directory.CreateDirectory(_before);
        Directory.CreateDirectory(_after);
    }

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string where, string name, string source) =>
        File.WriteAllText(Path.Combine(where, name), source);

    private IReadOnlyList<Reckoning> Compare() => new Hindsight().Compare(_before, _after);

    /// <summary>
    /// The same comparison against a catalogue that only knows what is named
    /// here.
    ///
    /// Needed because the shipped catalogue now has an opinion about every
    /// package the surface can see, so silence cannot be reached by picking one
    /// it forgot. Injecting the judgement is also the honest way to test what
    /// happens without it, rather than relying on a gap that a later commit
    /// would close.
    /// </summary>
    private IReadOnlyList<Reckoning> ComparedKnowing(params (string Package, bool Strands)[] known)
    {
        var path = Path.Combine(_work, $"stranded-{Guid.NewGuid():N}.json");

        File.WriteAllText(path, "{" + string.Join(",", known.Select(entry =>
            $$"""
              "{{entry.Package}}": { "strands": {{entry.Strands.ToString().ToLowerInvariant()}},
                                     "why": "written by a test" }
              """)) + "}");

        var strandings = Strandings.Load(path);

        // A fixture that will not parse is reported as an empty catalogue,
        // which is right for the tool and useless for a test: everything would
        // come back unjudged and the assertion would pass for the wrong reason.
        Assert.Equal(known.Length, strandings.Count);

        return new Hindsight(strandings: strandings).Compare(_before, _after);
    }

    private Reckoning Of(string package) => Compare().Single(r => r.Package == package);

    private const string UsesMvc = """
        using System.Web.Mvc;

        public class HomeController : Controller
        {
            public ActionResult Index() => View();
        }
        """;

    /// <summary>
    /// Newtonsoft types the successor does not have, which is the point.
    ///
    /// `JsonSerializer` would have been the wrong choice: System.Text.Json has
    /// one too, so the catalogue covers it and the coverage comes out high. The
    /// case this fixture exists for is the opposite one, where the tool says
    /// the move is a rewrite in disguise.
    /// </summary>
    private const string UsesNewtonsoft = """
        using Newtonsoft.Json;
        using Newtonsoft.Json.Linq;

        public class Store
        {
            public JObject Parse(string raw) => JObject.Parse(raw);

            public JToken Pick(JObject from) => from["id"];
        }
        """;

    [Fact]
    public void A_dependency_that_went_where_it_was_told_to_go_is_ported()
    {
        Write(_before, "Home.cs", UsesMvc);
        Write(_after, "Home.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class HomeController : Controller
            {
                public IActionResult Index() => View();
            }
            """);

        var reckoning = Of("Microsoft.AspNet.Mvc");

        Assert.Equal(Fate.Ported, reckoning.Became);
        Assert.Equal(1, reckoning.FilesOnProposed);
        Assert.Equal(0, reckoning.UsesAfter);
    }

    [Fact]
    public void A_dependency_still_there_afterwards_was_kept()
    {
        Write(_before, "Store.cs", UsesNewtonsoft);
        Write(_after, "Store.cs", UsesNewtonsoft);

        var reckoning = Of("Newtonsoft.Json");

        Assert.Equal(Fate.Kept, reckoning.Became);
        Assert.True(reckoning.UsesAfter > 0);
    }

    [Fact]
    public void A_migration_caught_in_the_middle_is_said_to_be_straddling()
    {
        // What an incremental migration looks like from outside, and the state
        // a before-and-after comparison would otherwise have to call one thing
        // or the other.
        Write(_before, "Home.cs", UsesMvc);
        Write(_after, "Old.cs", UsesMvc);
        Write(_after, "New.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class NewController : Controller
            {
                public IActionResult Index() => View();
            }
            """);

        Assert.Equal(Fate.Straddling, Of("Microsoft.AspNet.Mvc").Became);
    }

    [Fact]
    public void A_dependency_that_went_somewhere_else_entirely_is_not_reported_as_ported()
    {
        // The distinction that keeps the score honest. Gone is not the same as
        // gone where we said, and counting the first as the second would let
        // the tool take credit for a decision it had nothing to do with.
        Write(_before, "Home.cs", UsesMvc);
        Write(_after, "Nothing.cs", "public class Nothing { }");

        var reckoning = Of("Microsoft.AspNet.Mvc");

        Assert.Equal(Fate.Dropped, reckoning.Became);
        Assert.Equal(0, reckoning.FilesOnProposed);
    }

    [Fact]
    public void A_package_that_cannot_stay_is_expected_to_be_gone()
    {
        // The prediction, and the only one marked. `System.Web.Mvc` is built on
        // something ASP.NET Core does not have, so staying is not one of the
        // options and the coverage number has no bearing on it.
        Write(_before, "Home.cs", UsesMvc);
        Write(_after, "Home.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class HomeController : Controller
            {
                public IActionResult Index() => View();
            }
            """);

        var reckoning = Of("Microsoft.AspNet.Mvc");

        Assert.True(reckoning.Strands);
        Assert.False(reckoning.Expected);
        Assert.False(reckoning.StillThere);
        Assert.True(reckoning.Agreed);
    }

    [Fact]
    public void A_package_that_runs_on_the_target_is_expected_to_stay()
    {
        // The other half, and it is a claim of equal weight. Newtonsoft ships a
        // modern build, so nothing forces the move and a team keeps what works.
        Write(_before, "Store.cs", UsesNewtonsoft);
        Write(_after, "Store.cs", UsesNewtonsoft);

        var reckoning = Of("Newtonsoft.Json");

        Assert.False(reckoning.Strands);
        Assert.True(reckoning.Expected);
        Assert.True(reckoning.StillThere);
        Assert.True(reckoning.Agreed);
    }

    [Fact]
    public void A_migration_in_progress_is_read_by_weight_and_not_by_presence()
    {
        // Both packages being present says nothing on its own. A stranded one
        // on its way out leaves a shrinking remainder; a library somebody kept
        // and added to does not shrink. Measured on real pairs: Umbraco's MVC
        // went from 1,106 uses to 18, and Smartstore's Newtonsoft went from
        // 1,045 to 1,572 with two files on the successor.
        // Ten files down to one, which is the order of magnitude a real port
        // leaves behind: Umbraco's MVC went from 1,106 uses to 18.
        foreach (var index in Enumerable.Range(0, 10))
            Write(_before, $"Controller{index}.cs", UsesMvc.Replace("HomeController", $"C{index}"));

        Write(_after, "Left.cs", UsesMvc);
        Write(_after, "New.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class NewController : Controller
            {
                public IActionResult Index() => View();
            }
            """);

        var reckoning = Of("Microsoft.AspNet.Mvc");

        Assert.Equal(Fate.Straddling, reckoning.Became);
        Assert.False(reckoning.StillThere);
    }

    [Fact]
    public void And_a_library_that_grew_alongside_the_successor_is_still_there()
    {
        Write(_before, "Store.cs", UsesNewtonsoft);
        Write(_after, "Store.cs", UsesNewtonsoft);
        Write(_after, "More.cs", UsesNewtonsoft);
        Write(_after, "Toe.cs", """
            using System.Text.Json;

            public class Toe
            {
                public JsonSerializerOptions Options() => null;
            }
            """);

        var reckoning = Of("Newtonsoft.Json");

        Assert.Equal(Fate.Straddling, reckoning.Became);
        Assert.True(reckoning.StillThere);
        Assert.True(reckoning.Agreed);
    }

    [Fact]
    public void A_package_nobody_has_judged_is_scored_in_neither_direction()
    {
        // A question that was never asked cannot be right or wrong, and folding
        // silence into the numerator or the denominator is how an agreement
        // rate stops meaning anything.
        Write(_before, "Home.cs", UsesMvc);
        Write(_before, "Store.cs", UsesNewtonsoft);
        Write(_after, "Store.cs", UsesNewtonsoft);

        var reckonings = ComparedKnowing(("Newtonsoft.Json", false));
        var unjudged = reckonings.Single(r => r.Package == "Microsoft.AspNet.Mvc");

        Assert.Null(unjudged.Strands);
        Assert.Null(unjudged.Expected);
        Assert.Null(unjudged.Agreed);

        var marking = Hindsight.Mark(reckonings);

        Assert.Equal(0, marking.Forced);
        Assert.Equal(1, marking.Chosen);
    }

    [Fact]
    public void The_heaviest_dependency_is_reported_first()
    {
        // The order the decisions mattered in. A package holding four thousand
        // calls and one holding thirty are not two rows of equal weight.
        Write(_before, "Home.cs", UsesMvc);
        Write(_before, "More.cs", UsesMvc);
        Write(_before, "Store.cs", UsesNewtonsoft);
        Write(_after, "Store.cs", UsesNewtonsoft);

        Assert.Equal("Microsoft.AspNet.Mvc", Compare()[0].Package);
    }

    [Fact]
    public void A_tree_that_is_not_there_is_said_so_rather_than_read_as_empty()
    {
        // An empty answer from a path nobody has is a migration where nothing
        // happened, which is the most misleading thing this could report.
        Assert.Throws<DirectoryNotFoundException>(
            () => new Hindsight().Compare(Path.Combine(_work, "nope"), _after));

        Assert.Throws<DirectoryNotFoundException>(
            () => new Hindsight().Compare(_before, Path.Combine(_work, "nope")));
    }

    [Fact]
    public void The_two_halves_of_the_score_are_never_blended()
    {
        Write(_before, "Home.cs", UsesMvc);
        Write(_before, "Store.cs", UsesNewtonsoft);
        Write(_before, "Data.cs", """
            using System.Data.Entity;

            public class Context : DbContext { }
            """);
        Write(_after, "Home.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class HomeController : Controller
            {
                public IActionResult Index() => View();
            }
            """);
        Write(_after, "Store.cs", UsesNewtonsoft);
        Write(_after, "Data.cs", """
            using System.Data.Entity;

            public class Context : DbContext { }
            """);

        var marking = Hindsight.Mark(Compare());

        // Two rates, never one. What the runtime forces is a prediction; what a
        // team may do either way is not, and a blended number would be true of
        // neither.
        Assert.Equal(1, marking.Forced);
        Assert.Equal(1, marking.ForcedHeld);
        Assert.Equal(2, marking.Chosen);
        Assert.Equal(2, marking.ChosenHeld);
    }
}
