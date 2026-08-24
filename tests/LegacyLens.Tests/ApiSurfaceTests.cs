using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// What a codebase uses of a package, rather than what the package offers.
///
/// The number that matters is the concentration. Six types carrying everything
/// is an afternoon; sixty spread evenly is a rewrite; and a total on its own
/// cannot tell the two apart, which is why "366 references" has never helped
/// anyone estimate anything.
/// </summary>
public class ApiSurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-surface-{Guid.NewGuid():N}");

    public ApiSurfaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string name, string source) =>
        File.WriteAllText(Path.Combine(_root, name), source);

    private UsageSurface Mvc() => new ApiSurface().Of(_root, "Microsoft.AspNet.Mvc");

    /* ---- what counts ---- */

    [Fact]
    public void A_file_that_imports_nothing_relevant_is_not_counted()
    {
        Write("Plain.cs", """
            using System;

            public class Plain
            {
                public string Name { get; set; }
            }
            """);

        var surface = Mvc();

        Assert.False(surface.Used);
        Assert.Equal(0, surface.Files);
    }

    [Fact]
    public void The_types_a_file_names_are_counted_against_the_package_it_imports()
    {
        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index() => View();
                public ActionResult About() => View();
            }
            """);

        var surface = Mvc();

        Assert.True(surface.Used);
        Assert.Equal(1, surface.Files);
        Assert.Contains(surface.Types, t => t.Name == "Controller");
        Assert.Equal(2, surface.Types.Single(t => t.Name == "ActionResult").Uses);
    }

    [Fact]
    public void A_type_the_solution_declares_is_the_codebases_own()
    {
        // Otherwise every controller in the estate is reported as part of the
        // package it inherits from, and the surface is meaningless.
        Write("Base.cs", """
            using System.Web.Mvc;

            public class BaseController : Controller { }
            """);

        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : BaseController { }
            """);

        var surface = Mvc();

        Assert.DoesNotContain(surface.Types, t => t.Name == "BaseController");
        Assert.Contains(surface.Types, t => t.Name == "Controller");
    }

    [Fact]
    public void What_the_language_supplies_is_not_a_dependency()
    {
        Write("Home.cs", """
            using System.Web.Mvc;
            using System.Collections.Generic;

            public class HomeController : Controller
            {
                public List<string> Names(int count) => new List<string>();
            }
            """);

        var names = Mvc().Types.Select(t => t.Name).ToList();

        Assert.DoesNotContain("List", names);
        Assert.DoesNotContain("string", names);
        Assert.DoesNotContain("int", names);
    }

    [Fact]
    public void An_attribute_is_a_use_like_any_other()
    {
        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                [HttpPost]
                public ActionResult Save() => View();
            }
            """);

        Assert.Contains(Mvc().Types, t => t.Name == "HttpPost");
    }

    [Fact]
    public void A_file_that_will_not_parse_stops_itself_rather_than_the_run()
    {
        Write("Broken.cs", "public class Broken { ");
        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller { }
            """);

        Assert.True(Mvc().Used);
    }

    [Fact]
    public void A_local_variable_is_not_a_type()
    {
        // The mistake that measuring Orchard found. Roslyn derives
        // IdentifierNameSyntax from TypeSyntax, so the obvious way to collect
        // types collects every identifier in every expression, and reports
        // `builder` and `result` as the most used types in the codebase.
        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index()
                {
                    var builder = Something();
                    var result = builder.Build();
                    return View(result);
                }
            }
            """);

        var names = Mvc().Types.Select(t => t.Name).ToList();

        Assert.DoesNotContain("builder", names);
        Assert.DoesNotContain("result", names);
        Assert.DoesNotContain("Something", names);
        Assert.Contains("ActionResult", names);
    }

    [Fact]
    public void A_member_being_called_is_not_a_type()
    {
        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index()
                {
                    Response.Cache.SetExpires(Now);
                    return View();
                }
            }
            """);

        var names = Mvc().Types.Select(t => t.Name).ToList();

        Assert.DoesNotContain("Cache", names);
        Assert.DoesNotContain("SetExpires", names);
        Assert.DoesNotContain("Now", names);
    }

    [Fact]
    public void A_type_inside_a_generic_counts_too()
    {
        // Stopping at the outer name misses most of what a codebase touches.
        Write("Home.cs", """
            using System.Web.Mvc;
            using System.Collections.Generic;

            public class HomeController : Controller
            {
                public IList<ActionResult> All() => null;
            }
            """);

        Assert.Contains(Mvc().Types, t => t.Name == "ActionResult");
    }

    [Fact]
    public void A_delegate_the_solution_declares_is_its_own_too()
    {
        // Roslyn's BaseTypeDeclarationSyntax covers classes, interfaces, enums
        // and records, and not delegates. Orchard's own Localizer was being
        // reported as one of the most used types in ASP.NET MVC because of it.
        Write("Localizer.cs", "public delegate string Localizer(string text);");

        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public Localizer T { get; set; }
            }
            """);

        Assert.DoesNotContain(Mvc().Types, t => t.Name == "Localizer");
    }

    [Fact]
    public void A_generic_parameter_belongs_to_nobody()
    {
        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public TResult Convert<TResult>(ActionResult result) => default;
            }
            """);

        var names = Mvc().Types.Select(t => t.Name).ToList();

        Assert.DoesNotContain("TResult", names);
        Assert.Contains("ActionResult", names);
    }

    /* ---- the number that decides the shape of the work ---- */

    [Fact]
    public void Concentration_says_how_many_types_carry_most_of_it()
    {
        // Eighty of ninety uses on one type: one type does the work.
        Assert.Equal(1, UsageSurface.ConcentrationOf([80, 5, 3, 2]));

        // Spread evenly, four of five are needed to reach four fifths.
        Assert.Equal(4, UsageSurface.ConcentrationOf([10, 10, 10, 10, 10]));

        Assert.Equal(0, UsageSurface.ConcentrationOf([]));
    }

    [Fact]
    public void A_package_used_in_one_place_is_a_different_job_from_one_used_everywhere()
    {
        for (var i = 0; i < 5; i++)
        {
            Write($"Plain{i}.cs", $"public class Plain{i} {{ }}");
        }

        Write("Only.cs", """
            using System.Web.Mvc;

            public class OnlyController : Controller
            {
                public ActionResult Index() => View();
            }
            """);

        var surface = Mvc();

        Assert.Equal(1, surface.Files);
        Assert.Equal(1, surface.FilesForMostOfIt);
    }

    [Fact]
    public void The_file_worth_showing_is_the_densest_rather_than_the_largest()
    {
        // The correction the first real run forced. Orchard's heaviest user of
        // ASP.NET MVC is 821 lines: a local model spends minutes on it and
        // nobody reads it. A short file with the same correspondences teaches
        // the same lesson in a screen.
        Write("Small.cs", """
            using System.Web.Mvc;

            public class SmallController : Controller
            {
                public ActionResult A() => View();
                public ActionResult B() => View();
            }
            """);

        var padding = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"// line {i}"));
        Write("Big.cs", $$"""
            using System.Web.Mvc;

            {{padding}}

            public class BigController : Controller
            {
                public ActionResult A() => View();
                public ActionResult B() => View();
                public ActionResult C() => View();
            }
            """);

        Assert.EndsWith("Small.cs", Mvc().Heaviest[0].Path);
    }

    [Fact]
    public void A_file_too_long_to_project_is_left_out_and_said_so()
    {
        // Offered and then refused is worse than not offered.
        var padding = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"// line {i}"));

        Write("Huge.cs", $$"""
            using System.Web.Mvc;

            {{padding}}

            public class HugeController : Controller
            {
                public ActionResult A() => View();
            }
            """);

        var surface = Mvc();

        Assert.Empty(surface.Heaviest);
        Assert.Contains(surface.Notes, n => n.Contains("longer than 400 lines"));
    }

    /* ---- what it admits ---- */

    [Fact]
    public void It_says_that_it_read_syntax_rather_than_a_compilation()
    {
        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller { }
            """);

        Assert.Contains(Mvc().Notes, n => n.Contains("not from a compilation"));
    }

    [Fact]
    public void A_file_importing_two_catalogued_packages_is_reported_as_ambiguous()
    {
        // Syntax cannot say which of the two a given type came from, and a
        // coverage figure built on a silent guess is the one that gets a
        // migration signed off.
        Write("Both.cs", """
            using System.Web.Mvc;
            using Newtonsoft.Json;

            public class BothController : Controller
            {
                public JsonSerializerSettings Settings() => new JsonSerializerSettings();
            }
            """);

        Assert.Contains(Mvc().Notes, n => n.Contains("import another catalogued package"));
    }

    [Fact]
    public void A_package_outside_the_catalogue_is_refused_rather_than_guessed()
    {
        var surface = new ApiSurface().Of(_root, "Some.Package.Nobody.Listed");

        Assert.False(surface.Used);
        Assert.Contains(surface.Notes, n => n.Contains("not in the catalogue"));
    }

    /* ---- across the catalogue ---- */

    [Fact]
    public void Every_catalogued_package_the_codebase_touches_is_reported_once()
    {
        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller { }
            """);

        Write("Serialiser.cs", """
            using Newtonsoft.Json;

            public class Serialiser
            {
                public JsonSerializerSettings Settings() => new JsonSerializerSettings();
            }
            """);

        var all = new ApiSurface().All(_root);

        Assert.Contains(all, s => s.Package == "Microsoft.AspNet.Mvc");
        Assert.Contains(all, s => s.Package == "Newtonsoft.Json");
        Assert.DoesNotContain(all, s => s.Package == "NHibernate");
    }

    [Fact]
    public void The_most_used_package_comes_first()
    {
        Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult A() => View();
                public ActionResult B() => View();
                public ActionResult C() => View();
            }
            """);

        Write("Serialiser.cs", """
            using Newtonsoft.Json;

            public class Serialiser
            {
                public JsonSerializerSettings Settings() => new JsonSerializerSettings();
            }
            """);

        Assert.Equal("Microsoft.AspNet.Mvc", new ApiSurface().All(_root)[0].Package);
    }

    [Fact]
    public void A_directory_that_is_not_there_is_said_rather_than_returned_empty()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => new ApiSurface().All(Path.Combine(_root, "nope")));
    }
}
