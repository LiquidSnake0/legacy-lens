using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The judgement about what can stay at all.
///
/// A different question from what replaces what, and a more decisive one: a
/// package with no life on the target goes whatever anyone would prefer, and
/// one that runs there unchanged only moves if somebody decides to move it.
///
/// The loading tests exist because of M14. A single-file publish extracts to a
/// temporary folder, the catalogue sat beside the binary, the binary looked in
/// the temporary folder, and every package came back with no candidate at all
/// with nothing anywhere saying why. A catalogue that cannot be found has to
/// say so rather than read as an empty one.
/// </summary>
public class StrandingTests
{
    [Fact]
    public void The_shipped_catalogue_is_found_and_has_something_in_it()
    {
        var strandings = Strandings.Load();

        Assert.True(strandings.Count > 0, $"the catalogue was not found: {strandings.Source}");
        Assert.EndsWith("stranded.json", strandings.Source);
    }

    [Fact]
    public void The_old_web_stack_cannot_stay()
    {
        var strandings = Strandings.Load();

        foreach (var package in new[]
                 {
                     "Microsoft.AspNet.Mvc", "Microsoft.AspNet.Razor", "Microsoft.AspNet.WebPages",
                     "Microsoft.AspNet.WebApi.Core", "Owin",
                 })
        {
            Assert.True(strandings.For(package)?.Strands, $"{package} has no life on ASP.NET Core");
        }
    }

    [Fact]
    public void A_library_with_a_modern_build_is_a_choice_and_not_a_sentence()
    {
        var strandings = Strandings.Load();

        foreach (var package in new[] { "Newtonsoft.Json", "log4net", "Autofac", "EntityFramework" })
        {
            var stranding = strandings.For(package);

            Assert.NotNull(stranding);
            Assert.False(stranding.Strands);
            Assert.NotEqual(string.Empty, stranding.Why);
        }
    }

    [Fact]
    public void Every_judgement_says_why()
    {
        // The catalogue is a set of opinions somebody signed. An opinion with
        // no reasoning beside it cannot be argued with, which is the one thing
        // a reader has to be able to do with it.
        var strandings = Strandings.Load();

        foreach (var package in new[] { "Microsoft.AspNet.Mvc", "NHibernate", "Microsoft.Web.Infrastructure" })
            Assert.False(string.IsNullOrWhiteSpace(strandings.For(package)?.Why));
    }

    [Fact]
    public void A_package_nobody_recorded_is_unknown_rather_than_safe()
    {
        Assert.Null(Strandings.Load().For("Some.Package.Nobody.Listed"));
    }

    [Fact]
    public void A_catalogue_that_is_not_there_says_so_rather_than_reading_as_empty()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json");

        var strandings = Strandings.Load(missing);

        Assert.Equal(0, strandings.Count);
        Assert.Contains(missing, strandings.Source);
    }

    [Fact]
    public void And_one_that_will_not_parse_says_that_too()
    {
        // The failure that matters more than a missing file, because a file is
        // there and looks fine. Reported as unreadable rather than as a
        // catalogue with no opinions in it.
        var broken = Path.Combine(Path.GetTempPath(), $"broken-{Guid.NewGuid():N}.json");
        File.WriteAllText(broken, "{ this is not json");

        try
        {
            var strandings = Strandings.Load(broken);

            Assert.Equal(0, strandings.Count);
            Assert.Contains("could not be read", strandings.Source);
        }
        finally
        {
            File.Delete(broken);
        }
    }

    [Fact]
    public void The_commentary_in_it_is_not_read_as_a_package()
    {
        // The file is written and edited by hand, so it carries its reasoning
        // in keys beginning with //. A strict read would either choke on them
        // or record them as packages called "//".
        var strandings = Strandings.Load();

        Assert.Null(strandings.For("//"));
        Assert.Null(strandings.For("//1"));
    }
}

/// <summary>
/// Bundling, which is its own package and had fallen through every net.
///
/// The surface counted its types as ASP.NET MVC's until M28 read the packages
/// and found they belong to Microsoft.AspNet.Web.Optimization. That was right,
/// and it made the question disappear: the package was in no catalogue, so
/// nothing measured it, nothing said it cannot stay, and nobody was asked what
/// to do about it.
/// </summary>
public class OptimizationPackageTests
{
    [Fact]
    public void It_cannot_stay_and_the_catalogue_says_why()
    {
        var stranding = Strandings.Load().For("Microsoft.AspNet.Web.Optimization");

        Assert.NotNull(stranding);
        Assert.True(stranding.Strands);
        Assert.Contains("System.Web", stranding.Why);
    }

    [Fact]
    public void Nothing_replaces_it_and_that_is_recorded_rather_than_left_unknown()
    {
        var successor = Assert.Single(
            Successors.Load().For("Microsoft.AspNet.Web.Optimization"));

        Assert.Equal(string.Empty, successor.Package);
        Assert.Contains("ScriptBundle", successor.Types.Keys);
        Assert.Null(successor.Types["ScriptBundle"]);
    }

    [Fact]
    public void And_it_is_not_recorded_as_withdrawn()
    {
        // The distinction M26 drew. A withdrawn feature leaves nothing to do.
        // The assets still have to be bundled somewhere, so this leaves
        // somebody with a problem and must not be priced as a deletion.
        var successor = Assert.Single(
            Successors.Load().For("Microsoft.AspNet.Web.Optimization"));

        Assert.Empty(successor.Removed);
    }

    [Fact]
    public void The_surface_knows_which_namespace_it_occupies()
    {
        Assert.Contains("System.Web.Optimization",
            ApiSurface.Namespaces["Microsoft.AspNet.Web.Optimization"]);
    }
}

/// <summary>
/// The three catalogues have to agree about which packages exist.
///
/// They drifted. `Microsoft.AspNet.Web.Optimization` was measured by nothing
/// until M28 uncovered it, and `Microsoft.AspNet.Razor`, the two Web API
/// packages and Owin were all recorded as having no life on modern .NET with
/// nothing recorded about what to do instead. The surface counted their usage
/// and the report answered silence.
/// </summary>
public class CatalogueAgreementTests
{
    [Fact]
    public void Every_package_the_surface_measures_is_judged_on_whether_it_can_stay()
    {
        var strandings = Strandings.Load();

        foreach (var package in ApiSurface.Namespaces.Keys)
        {
            Assert.True(strandings.For(package) is not null,
                $"{package} is measured and nobody has said whether it can stay");
        }
    }

    [Fact]
    public void Every_package_that_cannot_stay_has_an_answer_recorded()
    {
        // Not necessarily a successor: "nothing replaces it" is an answer and
        // an empty candidate says so. Silence is not.
        var catalogue = Successors.Load();
        var strandings = Strandings.Load();

        foreach (var package in ApiSurface.Namespaces.Keys)
        {
            if (strandings.For(package)?.Strands != true) continue;

            Assert.True(catalogue.For(package).Count > 0,
                $"{package} cannot stay and the catalogue says nothing about what to do");
        }
    }

    [Fact]
    public void And_a_package_that_can_stay_is_not_pretended_to_be_urgent()
    {
        // Newtonsoft, log4net, Autofac, EF6 and NHibernate all run on modern
        // .NET. Recording a successor for them is right, and recording them as
        // stranded would price a choice as a sentence.
        var strandings = Strandings.Load();

        foreach (var package in new[] { "Newtonsoft.Json", "log4net", "Autofac", "EntityFramework", "NHibernate" })
            Assert.False(strandings.For(package)?.Strands, $"{package} runs on modern .NET");
    }
}
