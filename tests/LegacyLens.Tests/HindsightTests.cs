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
    public void A_high_coverage_that_was_taken_up_counts_as_agreement()
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

        Assert.True(reckoning.Coverage >= Hindsight.Substitutable);
        Assert.True(reckoning.Agreed);
    }

    [Fact]
    public void A_low_coverage_that_was_kept_also_counts_as_agreement()
    {
        // Both halves of the claim are claims. Saying "this one is a rewrite,
        // they will not do it" is worth as much as the other, and a score that
        // only counted the ports would be measuring enthusiasm.
        Write(_before, "Store.cs", UsesNewtonsoft);
        Write(_after, "Store.cs", UsesNewtonsoft);

        var reckoning = Of("Newtonsoft.Json");

        Assert.True(reckoning.Coverage < Hindsight.Substitutable);
        Assert.True(reckoning.Agreed);
    }

    [Fact]
    public void A_package_the_catalogue_says_nothing_about_is_never_scored()
    {
        // A question that was never asked cannot be right or wrong, and folding
        // silence into the numerator or the denominator is how an agreement
        // rate becomes meaningless.
        Write(_before, "Data.cs", """
            using System.Data.Entity;

            public class Context : DbContext { }
            """);
        Write(_after, "Data.cs", """
            using System.Data.Entity;

            public class Context : DbContext { }
            """);

        var reckoning = Of("EntityFramework");

        Assert.Null(reckoning.Proposed);
        Assert.Null(reckoning.Agreed);
        Assert.Null(Hindsight.Agreement([reckoning]));
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
    public void The_agreement_rate_counts_only_what_was_claimed()
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

        var agreement = Hindsight.Agreement(Compare());

        Assert.NotNull(agreement);
        Assert.Equal(2, agreement.Value.Judged);
        Assert.Equal(2, agreement.Value.Agreed);
    }
}
