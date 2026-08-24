using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Where the questions point.
///
/// A question with no reference to the code is a generic questionnaire, and it
/// reads as one by the second screen: the reader can tell nothing was read
/// before they were asked.
/// </summary>
public class DilemmaSitesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-sites-{Guid.NewGuid():N}");

    public DilemmaSitesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string name, string source) =>
        File.WriteAllText(Path.Combine(_root, name), source);

    private string Catalogue()
    {
        var path = Path.Combine(_root, "dilemmas.json");
        File.WriteAllText(path, """
            {
              "state": {
                "name": "Where state goes",
                "triggers": ["HttpSessionState", "HttpContext", "SessionStateAttribute"],
                "outcomes": [ { "id": "a" }, { "id": "b" } ],
                "questions": []
              },
              "unrelated": {
                "name": "Never raised here",
                "triggers": ["SomethingElse"],
                "outcomes": [ { "id": "x" } ],
                "questions": []
              }
            }
            """);
        return path;
    }

    private IReadOnlyList<Raised> Find() =>
        new DilemmaSites().Find(_root, Dilemmas.Load(Catalogue()));

    [Fact]
    public void Only_the_dilemmas_the_code_actually_raises_come_back()
    {
        Write("Cart.cs", """
            public class Cart
            {
                public object Get() => HttpContext.Current.Session["cart"];
            }
            """);

        var raised = Assert.Single(Find());
        Assert.Equal("state", raised.Dilemma.Id);
    }

    [Fact]
    public void A_site_carries_the_line_and_what_is_on_it()
    {
        // The whole point: the reader is told where to look, not asked in the
        // abstract how they handle sessions.
        Write("Cart.cs", """
            public class Cart
            {
                public object Get()
                {
                    return HttpContext.Current.Session["cart"];
                }
            }
            """);

        var site = Assert.Single(Find()[0].Sites);

        Assert.Equal(5, site.Line);
        Assert.Equal("HttpContext", site.Name);
        Assert.Contains("Session[\"cart\"]", site.Text);
        Assert.EndsWith("Cart.cs", site.Path);
    }

    [Fact]
    public void A_name_used_as_an_expression_counts_here_even_though_it_is_not_a_type_position()
    {
        // Deliberately looser than the usage surface. There the question was
        // how much of a package is used, and a member access would inflate it;
        // here the question is where to point somebody.
        Write("Thing.cs", "public class Thing { public object C => HttpContext.Current; }");

        Assert.Single(Find()[0].Sites);
    }

    [Fact]
    public void A_name_inside_a_longer_one_is_not_a_site()
    {
        Write("Thing.cs", "public class HttpContextHelperFactory { public int N => 1; }");

        Assert.Empty(Find());
    }

    [Fact]
    public void Files_are_counted_as_well_as_mentions()
    {
        Write("A.cs", "public class A { public object C => HttpContext.Current; }");
        Write("B.cs", "public class B { public object C => HttpContext.Current; }");

        var raised = Find()[0];
        Assert.Equal(2, raised.Sites.Count);
        Assert.Equal(2, raised.Files);
    }

    [Fact]
    public void A_file_that_will_not_parse_stops_itself_rather_than_the_run()
    {
        Write("Broken.cs", "public class Broken {");
        Write("Cart.cs", "public class Cart { public object C => HttpContext.Current; }");

        Assert.Single(Find()[0].Sites);
    }

    [Fact]
    public void A_codebase_that_raises_nothing_returns_nothing_rather_than_empty_dilemmas()
    {
        Write("Plain.cs", "public class Plain { public int N => 1; }");

        Assert.Empty(Find());
    }

    [Fact]
    public void A_directory_that_is_not_there_is_said_rather_than_returned_empty()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => new DilemmaSites().Find(Path.Combine(_root, "nope"), Dilemmas.Load(Catalogue())));
    }

    [Fact]
    public void An_attribute_written_the_short_way_is_still_found()
    {
        // Found by running it, not by a test. C# lets `[SessionState]` mean
        // SessionStateAttribute, and the short form is the one people write, so
        // a catalogue naming the type read a textbook session controller and
        // raised nothing at all.
        Write("Cart.cs", """
            public class Cart
            {
                [SessionState]
                public int Index() => 1;
            }
            """);

        var site = Assert.Single(Find()[0].Sites);

        Assert.Equal("SessionState", site.Name);
        Assert.Equal(3, site.Line);
    }

    [Fact]
    public void A_using_is_not_somewhere_a_reader_can_go_and_see_the_problem()
    {
        // Also found by running it. The last segment of a namespace matched,
        // which put the top of every file in the list beside the lines that
        // actually do something.
        Write("Cart.cs", """
            using System.Web.SessionState;

            namespace Shop.SessionState
            {
                public class Cart
                {
                    public object Get() => HttpContext.Current.Session;
                }
            }
            """);

        var site = Assert.Single(Find()[0].Sites);

        Assert.Equal("HttpContext", site.Name);
        Assert.Equal(7, site.Line);
    }

    [Fact]
    public void One_line_is_one_place_to_look_however_many_triggers_are_on_it()
    {
        // Found on screen. Two triggers on one line printed the same line
        // twice, which reads as a bug in the tool rather than as two findings.
        Write("Cart.cs", """
            public class Cart
            {
                public object Get() => HttpContext.Current.HttpSessionState;
            }
            """);

        var site = Assert.Single(Find()[0].Sites);

        Assert.Equal(3, site.Line);
    }

    [Fact]
    public void Sites_are_capped_so_a_large_estate_does_not_return_a_transcript()
    {
        for (var i = 0; i < 40; i++)
        {
            Write($"File{i}.cs", $"public class File{i} {{ public object C => HttpContext.Current; }}");
        }

        Assert.Equal(12, Find()[0].Sites.Count);
    }
}
