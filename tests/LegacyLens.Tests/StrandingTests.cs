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
