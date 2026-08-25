using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The catalogue's type correspondences, held against what teams wrote.
///
/// One level below the package question. That one asks whether a dependency
/// moved at all; this asks whether `ActionResult` becoming `IActionResult` is
/// what four real migrations actually did, and what they did that nobody has
/// written down.
/// </summary>
public class CorrespondenceTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(), $"lens-correspondence-{Guid.NewGuid():N}");

    private readonly string _before;
    private readonly string _after;

    public CorrespondenceTests()
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

    private IReadOnlyList<Correspondence> Compare() =>
        new Correspondences().Compare(_before, _after);

    private Correspondence Of(string type) => Compare().First(c => c.Type == type);

    [Fact]
    public void A_recorded_correspondence_the_team_wrote_is_confirmed()
    {
        Write(_before, "Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index() => View();
            }
            """);

        Write(_after, "Home.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class HomeController : Controller
            {
                public IActionResult Index() => View();
            }
            """);

        var found = Of("ActionResult");

        Assert.Equal("IActionResult", found.Recorded);
        Assert.True(found.CounterpartSeen);
    }

    [Fact]
    public void An_attribute_is_found_under_the_name_it_is_written_with()
    {
        // The catalogue records the declared name, `HttpPostAttribute`, and
        // nobody writes it: a use is `[HttpPost]`. Compared literally, the six
        // hundred and eight uses of the commonest attribute in ASP.NET MVC came
        // back as a correspondence nobody had taken up.
        Write(_before, "Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                [HttpPost]
                public ActionResult Save() => View();
            }
            """);

        Write(_after, "Home.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class HomeController : Controller
            {
                [HttpPost]
                public IActionResult Save() => View();
            }
            """);

        var found = Of("HttpPost");

        Assert.Equal("HttpPostAttribute", found.Recorded);
        Assert.True(found.CounterpartSeen);
    }

    [Fact]
    public void A_name_the_catalogue_does_not_have_and_the_successor_does_is_a_candidate()
    {
        // Most of a framework move is transcription: the type kept its name and
        // changed namespace. Read out of a real migration, those are candidates
        // for a person to sign.
        // `ActionContext` rather than `TagBuilder`: the latter was the example
        // here until four real migrations put it into the catalogue, which is
        // the whole point of this and also what made this fixture stop testing
        // anything.
        Write(_before, "Helper.cs", """
            using System.Web.Mvc;

            public class Helper
            {
                public ActionContext Where() => null;
            }
            """);

        Write(_after, "Helper.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class Helper
            {
                public ActionContext Where() => null;
            }
            """);

        var found = Of("ActionContext");

        Assert.Null(found.Recorded);
        Assert.True(found.SameNameSeen);
        Assert.True(found.Candidate);
    }

    [Fact]
    public void A_name_that_is_nowhere_in_the_finished_code_is_not_a_candidate()
    {
        Write(_before, "Old.cs", """
            using System.Web.Mvc;

            public class Old
            {
                public HttpUnauthorizedResult Deny() => null;
            }
            """);

        Write(_after, "New.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class New
            {
                public IActionResult Deny() => null;
            }
            """);

        var found = Of("HttpUnauthorizedResult");

        Assert.False(found.SameNameSeen);
        Assert.False(found.Candidate);
    }

    [Fact]
    public void The_mark_counts_only_correspondences_the_old_code_exercised()
    {
        // Marking an entry nobody touched would be marking the catalogue on
        // breadth rather than on being right.
        Write(_before, "Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index() => View();
            }
            """);

        Write(_after, "Home.cs", """
            using Microsoft.AspNetCore.Mvc;

            public class HomeController : Controller
            {
                public IActionResult Index() => View();
            }
            """);

        var (confirmed, recorded) = Correspondences.Mark(Compare());

        Assert.True(recorded < 10, $"only what was used is marked, not the whole catalogue: {recorded}");
        Assert.True(confirmed > 0);
        Assert.True(confirmed <= recorded);
    }

    [Fact]
    public void A_tree_that_is_not_there_is_said_so()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => new Correspondences().Compare(Path.Combine(_work, "nope"), _after));
    }
}
