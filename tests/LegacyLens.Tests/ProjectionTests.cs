using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The compiler as the judge of a projection.
///
/// The model writes the rewritten file; this decides whether it is worth
/// showing anyone. That division exists because the failure reported against
/// every generative migration tool is references to things that do not exist,
/// and a compiler settles that in milliseconds and cannot be argued with.
/// </summary>
public class ProjectionTests
{
    [Fact]
    public void Code_that_compiles_is_reported_as_compiling()
    {
        var verdict = new Projection().Compile("""
            public class Pricing
            {
                public decimal Total(decimal net, decimal rate) => net * (1 + rate);
            }
            """);

        Assert.True(verdict.Compiles, string.Join("\n", verdict.Errors));
        Assert.Empty(verdict.Errors);
    }

    [Fact]
    public void An_invented_type_is_named_rather_than_buried_in_the_errors()
    {
        // The finding this whole class exists for. A model writing modern code
        // invents plausible names, and "does not exist" is a different problem
        // from a missing semicolon.
        var verdict = new Projection().Compile("""
            public class Controller
            {
                public IActionResultFactory Make() => null;
            }
            """);

        Assert.False(verdict.Compiles);
        Assert.Contains("IActionResultFactory", verdict.Invented);
    }

    [Fact]
    public void A_name_that_exists_but_was_not_imported_is_not_an_invention()
    {
        // The distinction the first real run forced. IActionResult exists; the
        // model forgot the namespace. Calling that an invention throws away a
        // correct name on the next attempt.
        var verdict = new Projection().Compile("""
            public class HomeController
            {
                public IActionResult Index() => null;
            }
            """);

        Assert.False(verdict.Compiles);
        Assert.True(verdict.Sound);
        Assert.Contains("IActionResult", verdict.Unimported);
        Assert.Empty(verdict.Invented);
    }

    [Fact]
    public void An_attribute_the_solution_declares_is_found_without_its_suffix()
    {
        // Attributes are written without the suffix and declared with it, so
        // [OrchardFeature] has to find OrchardFeatureAttribute.
        var verdict = new Projection().Compile("""
            [OrchardFeature("x")]
            public class Thing { }
            """,
            new HashSet<string> { "OrchardFeatureAttribute" });

        Assert.True(verdict.Sound);
        Assert.Contains("OrchardFeature", verdict.FromProject);
    }

    [Fact]
    public void A_member_that_does_not_exist_does_not_become_an_invented_type()
    {
        // CS0117 and CS1061 quote two names, and reading the first out of them
        // put entries like `the` in a list of invented types.
        var verdict = new Projection().Compile("""
            public class Thing
            {
                public void Go() => "text".NoSuchMethod();
            }
            """);

        Assert.False(verdict.Compiles);
        Assert.DoesNotContain("the", verdict.Invented);
    }

    [Fact]
    public void An_invented_namespace_is_named_too()
    {
        var verdict = new Projection().Compile("""
            using Microsoft.AspNetCore.Mvc.Superpowers;

            public class Thing { }
            """);

        Assert.False(verdict.Compiles);
        Assert.NotEmpty(verdict.Invented);
    }

    [Fact]
    public void A_syntax_error_is_a_failure_but_not_an_invented_name()
    {
        // Kept apart on purpose: one is a typo, the other is the failure mode
        // this tool exists to be different from.
        var verdict = new Projection().Compile("public class Broken { ");

        Assert.False(verdict.Compiles);
        Assert.NotEmpty(verdict.Errors);
        Assert.Empty(verdict.Invented);
    }

    [Fact]
    public void Nothing_to_compile_is_said_rather_than_passed()
    {
        var verdict = new Projection().Compile("   ");

        Assert.False(verdict.Compiles);
        Assert.Contains(verdict.Errors, e => e.Contains("nothing to compile"));
    }

    [Fact]
    public void A_projection_needs_no_entry_point()
    {
        // An excerpt is not a program, and demanding a Main would fail every
        // projection for the wrong reason.
        Assert.True(new Projection().Compile("public class Excerpt { }").Compiles);
    }

    [Fact]
    public void The_claim_it_makes_is_no_larger_than_what_was_checked()
    {
        var verdict = new Projection().Compile("public class Fine { }");

        Assert.Contains("Behaviour not verified", verdict.Claim);
        Assert.DoesNotContain("migrated", verdict.Claim);
    }

    [Fact]
    public void What_it_compiled_against_is_named_rather_than_assumed()
    {
        // "Compiles" means nothing without it, and the answer changes with the
        // machine it ran on.
        Assert.Contains(".NET", new Projection().Compile("public class A { }").Target);
    }

    [Fact]
    public void Aspnet_core_is_available_to_compile_against_because_this_runs_on_it()
    {
        // The reason no SDK, no restore and no network are needed: the target
        // framework is present because the tool is running on it. If this ever
        // fails, projections of web code silently become unverifiable.
        Assert.True(Projection.Available("Microsoft.AspNetCore.Mvc.Core"));
        Assert.False(Projection.Available("Some.Assembly.Nobody.Ships"));
    }

    [Fact]
    public void A_real_aspnet_core_controller_compiles()
    {
        // The shape a projection actually takes: what an ASP.NET MVC 5
        // controller becomes. If this stops compiling, the target moved.
        var verdict = new Projection().Compile("""
            using Microsoft.AspNetCore.Mvc;

            public class HomeController : Controller
            {
                [HttpGet]
                public IActionResult Index() => View();

                [HttpPost]
                public IActionResult Save(string name)
                {
                    if (string.IsNullOrEmpty(name)) return BadRequest();
                    return RedirectToAction(nameof(Index));
                }
            }
            """);

        Assert.True(verdict.Compiles, string.Join("\n", verdict.Errors));
    }

    [Fact]
    public void The_same_controller_written_for_the_old_framework_does_not()
    {
        // System.Web.Mvc is not on modern .NET, which is the whole reason any
        // of this exists. A projection that still names it has not moved.
        var verdict = new Projection().Compile("""
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index() => View();
            }
            """);

        Assert.False(verdict.Compiles);
        Assert.NotEmpty(verdict.Invented);
    }
}
