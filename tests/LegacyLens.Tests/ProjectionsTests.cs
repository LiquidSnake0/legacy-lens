using LegacyLens.Api.Generation;

namespace LegacyLens.Tests;

/// <summary>
/// The bridge between the catalogue and the compiler.
///
/// What is worth pinning here is not the model's output, which is not
/// deterministic, but what surrounds it: which correspondences get handed over,
/// that a failure is reported as a failure, and that a fenced answer does not
/// look like a syntax error.
/// </summary>
public class ProjectionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-project-{Guid.NewGuid():N}");

    public ProjectionsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string source)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static Projections With(params string[] answers) =>
        new(new OneChat(new Scripted(answers)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Projections>.Instance);

    /* ---- the fence ---- */

    [Fact]
    public void A_fenced_answer_is_unwrapped_before_it_reaches_the_compiler()
    {
        // The prompt asks for no fence and models add one anyway. A fence
        // reaching the compiler is a syntax error that looks like the
        // projection failed, when what failed was following an instruction.
        Assert.Equal(
            "public class A { }",
            Projections.Unfence("```csharp\npublic class A { }\n```"));

        Assert.Equal(
            "public class A { }",
            Projections.Unfence("```\npublic class A { }\n```"));
    }

    [Fact]
    public void An_unfenced_answer_is_left_alone()
    {
        Assert.Equal("public class A { }", Projections.Unfence("  public class A { }  "));
    }

    /* ---- what happens around the model ---- */

    [Fact]
    public async Task A_projection_that_compiles_is_reported_with_the_claim_it_earned()
    {
        var path = Write("Home.cs", "using System.Web.Mvc; public class Home : Controller { }");

        var result = await With("public class Home { }").ProjectAsync(path, "Microsoft.AspNet.Mvc");

        Assert.True(result.Verdict.Compiles);
        Assert.Equal(1, result.Attempts);
        Assert.Contains("Behaviour not verified", result.Verdict.Claim);
    }

    [Fact]
    public async Task A_projection_that_never_compiles_is_shown_as_a_failure()
    {
        // Not quietly handed over. A projection that does not compile names
        // types that do not exist, which is the failure this tool exists to be
        // different from.
        var path = Write("Home.cs", "using System.Web.Mvc; public class Home : Controller { }");

        var result = await With(
            "public class Home : IInventedBase { }",
            "public class Home : IStillInvented { }")
            .ProjectAsync(path, "Microsoft.AspNet.Mvc");

        Assert.False(result.Verdict.Compiles);
        Assert.Equal(2, result.Attempts);
        Assert.NotEmpty(result.Verdict.Invented);
        Assert.Contains(result.Notes, n => n.Contains("names things that exist nowhere"));
    }

    [Fact]
    public async Task A_second_attempt_is_made_with_the_compiler_s_answer()
    {
        var path = Write("Home.cs", "using System.Web.Mvc; public class Home : Controller { }");

        var result = await With(
            "public class Home : IInvented { }",
            "public class Home { }")
            .ProjectAsync(path, "Microsoft.AspNet.Mvc");

        Assert.True(result.Verdict.Compiles);
        Assert.Equal(2, result.Attempts);
    }

    /* ---- what the model is told ---- */

    [Fact]
    public async Task Only_the_correspondences_this_file_uses_are_handed_over()
    {
        // Handing over the whole catalogue buries the six lines that matter in
        // ninety.
        var path = Write("Home.cs", """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index() => View();
            }
            """);

        var result = await With("public class HomeController { }")
            .ProjectAsync(path, "Microsoft.AspNet.Mvc");

        Assert.Contains(result.Given, g => g.StartsWith("ActionResult becomes"));
        Assert.Contains(result.Given, g => g.StartsWith("Controller becomes"));
        Assert.DoesNotContain(result.Given, g => g.StartsWith("JsonResult"));
    }

    [Fact]
    public async Task A_type_with_no_replacement_is_handed_over_as_such()
    {
        // So the model writes a TODO naming what was lost rather than inventing
        // a substitute, which is the whole reason the catalogue records nulls.
        var path = Write("Old.cs", """
            using System.Web.Mvc;

            public class OldController : Controller
            {
                [ChildActionOnly]
                public ActionResult Panel() => View();
            }
            """);

        var result = await With("public class OldController { }")
            .ProjectAsync(path, "Microsoft.AspNet.Mvc");

        Assert.Contains(result.Given, g => g == "ChildActionOnly: nothing replaces it");
    }

    [Fact]
    public async Task A_name_inside_a_longer_word_is_not_a_use_of_it()
    {
        // `View` matching inside `ViewModelBuilder` would hand over a
        // correspondence the file never uses.
        var path = Write("Builder.cs", """
            public class ControllerFactoryBuilder
            {
                public string Name => "x";
            }
            """);

        var result = await With("public class ControllerFactoryBuilder { }")
            .ProjectAsync(path, "Microsoft.AspNet.Mvc");

        Assert.DoesNotContain(result.Given, g => g.StartsWith("Controller becomes"));
    }

    [Fact]
    public async Task A_package_with_no_catalogued_successor_says_so()
    {
        var path = Write("Thing.cs", "public class Thing { }");

        var result = await With("public class Thing { }")
            .ProjectAsync(path, "Some.Package.Nobody.Catalogued");

        Assert.Contains(result.Notes, n => n.Contains("No successor is catalogued"));
    }

    /* ---- doubles ---- */

    private sealed class Scripted(string[] answers) : IChatClient
    {
        private int _asked;

        public Task<string> CompleteAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(answers[Math.Min(_asked++, answers.Length - 1)]);

        public async IAsyncEnumerable<string> StreamAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return await CompleteAsync(prompt, ct);
        }
    }

    private sealed class OneChat(IChatClient chat) : IChatClients
    {
        public IChatClient For(ModelChoice? choice) => chat;
        public ModelOptions Options => new("scripted", false, "", "");
    }
}
