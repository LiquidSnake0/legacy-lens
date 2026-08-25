using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// What the target framework says about the types nobody catalogued.
///
/// The catalogue stays hand-written, because what replaces what is a judgement.
/// The column beside it is not a judgement at all: whether modern .NET still
/// has a type of that name is a question of fact, and the framework is loaded
/// into this process already.
///
/// The tests that matter are the ones where being wrong is expensive. A name
/// that survived into an unrelated namespace must never be reported as an
/// answer, and a type the base library still provides must never be counted as
/// work.
/// </summary>
public class UnlistedTests
{
    private static ApiUse Use(string name, int uses = 1) => new(name, uses, 1);

    private static UnlistedReading Read(string successor, params ApiUse[] types) =>
        new Unlisted().Read(types, successor);

    [Fact]
    public void A_type_named_in_the_successor_is_offered_as_a_lead()
    {
        var reading = Read("Microsoft.AspNetCore.Mvc", Use("TagBuilder"));

        var found = Assert.Single(reading.Of(Standing.InSuccessor));

        Assert.Equal("Microsoft.AspNetCore.Mvc.Rendering.TagBuilder", found.Where);
    }

    [Fact]
    public void The_same_name_somewhere_unrelated_is_a_trap_and_never_an_answer()
    {
        // The assertion this file exists for. `System.Web.HttpContext` and
        // `Microsoft.AspNetCore.Http.HttpContext` share a word and nothing else,
        // and that pair is the hardest part of an ASP.NET migration rather than
        // a rename. Reported as a correspondence it would send somebody into
        // the worst of the work believing it was done.
        var reading = Read("Microsoft.AspNetCore.Mvc", Use("HttpContext"));

        var found = Assert.Single(reading.Of(Standing.Elsewhere));

        Assert.Equal("HttpContext", found.Use.Name);
        Assert.Empty(reading.Of(Standing.InSuccessor));
        Assert.Contains("Http", found.Where);
    }

    [Fact]
    public void A_name_the_framework_does_not_have_at_all_is_the_finding()
    {
        var reading = Read("Microsoft.AspNetCore.Mvc", Use("HttpUnauthorizedResult", 408));

        var gone = Assert.Single(reading.Of(Standing.Gone));

        Assert.Null(gone.Where);
        Assert.Equal(408, reading.Uses(Standing.Gone));
    }

    [Fact]
    public void What_is_left_to_decide_counts_the_traps_as_well_as_the_gone()
    {
        // A trap is not an answer, so it is still work. Counting it as settled
        // because a word matched is the whole thing this avoids.
        var reading = Read("Microsoft.AspNetCore.Mvc",
            Use("TagBuilder"), Use("HttpContext"), Use("HttpUnauthorizedResult"));

        Assert.Equal(2, reading.Left);
    }

    [Fact]
    public void A_candidate_whose_answer_is_deletion_has_no_successor_to_look_inside()
    {
        // Microsoft.Web.Infrastructure has no successor because nothing needs
        // to succeed it. Looking for a namespace named after an empty string
        // would match everything.
        var reading = Read(string.Empty, Use("TagBuilder"));

        Assert.Empty(reading.Of(Standing.InSuccessor));
        Assert.Single(reading.Of(Standing.Elsewhere));
    }

    [Fact]
    public void The_hard_one_is_still_a_trap_after_that()
    {
        // The exclusion was doing one useful thing by accident, and this is the
        // case it was protecting. It is protected properly instead: HttpContext
        // resolves only to Microsoft.AspNetCore.Http on the target, which is not
        // System.*, so nothing has to be excluded for it to land in Elsewhere.
        var reading = Read("Microsoft.AspNetCore.Mvc", Use("HttpContext"));

        Assert.Single(reading.Of(Standing.Elsewhere));
        Assert.Empty(reading.Of(Standing.InSuccessor));
    }

    [Fact]
    public void Nothing_here_is_written_back_into_the_catalogue()
    {
        // The generated part has to stay visibly apart from the written part.
        // A reading is returned to the caller and never merged into a
        // Coverage's covered column, where it would become indistinguishable
        // from a judgement somebody signed.
        var catalogue = Successors.Load();
        var before = catalogue.For("Microsoft.AspNet.Mvc").FirstOrDefault();

        // `ActionContext` rather than `TagBuilder`: the latter is in the
        // catalogue now, put there by reading four finished migrations, which
        // is a different thing from a reading writing itself back at run time
        // and would have made this assertion pass for the wrong reason.
        Read("Microsoft.AspNetCore.Mvc", Use("ActionContext"));

        var after = Successors.Load().For("Microsoft.AspNet.Mvc").FirstOrDefault();

        Assert.Equal(before?.Types.Count, after?.Types.Count);
        Assert.False(after?.Types.ContainsKey("ActionContext"));
    }

    [Fact]
    public void An_empty_column_reads_as_empty_rather_than_as_settled()
    {
        var reading = Read("Microsoft.AspNetCore.Mvc");

        Assert.Empty(reading.Types);
        Assert.Equal(0, reading.Left);
    }

    [Fact]
    public void A_successor_that_is_a_package_rather_than_the_framework_is_not_asked()
    {
        // The defect this closes. log4net's answer is Serilog, which nothing in
        // the runtime carries, so every type of every predecessor came back
        // under "the framework does not have at all". Literally true, and a
        // reader concludes that twenty-two types are gone when what happened is
        // that the question could not be asked.
        var reading = Read("Serilog", Use("ILog"), Use("LogManager"));

        Assert.False(reading.Applicable);
        Assert.Empty(reading.Types);
        Assert.Equal(0, reading.Left);
    }

    [Fact]
    public void A_successor_the_framework_carries_is_asked_as_before()
    {
        var reading = Read("Microsoft.AspNetCore.Mvc", Use("TagBuilder"));

        Assert.True(reading.Applicable);
        Assert.Single(reading.Of(Standing.InSuccessor));
    }

    [Fact]
    public void The_framework_knows_which_of_the_two_it_carries()
    {
        Assert.True(FrameworkTypes.Carries("Microsoft.AspNetCore.Mvc"));
        Assert.True(FrameworkTypes.Carries("System.Text.Json"));
        Assert.False(FrameworkTypes.Carries("Serilog"));
        Assert.False(FrameworkTypes.Carries("Autofac"));
        Assert.False(FrameworkTypes.Carries(string.Empty));
    }

    [Fact]
    public void An_attribute_is_found_under_the_name_it_is_written_with()
    {
        // A use written `[AcceptVerbs]` is recorded under the short spelling
        // everywhere else in this tool, because that is how C# is written. The
        // framework declares `AcceptVerbsAttribute`, so asking it about the
        // short one on its own answers no.
        //
        // Measured on nopCommerce 3.90: AcceptVerbs and ModelBinder were
        // reported as types modern .NET does not have at all, and both are in
        // Microsoft.AspNetCore.Mvc. The same rule M20 found missing on the
        // declaration side; two places out of four had it.
        var reading = Read("Microsoft.AspNetCore.Mvc", Use("AcceptVerbs"));

        var found = Assert.Single(reading.Of(Standing.InSuccessor));

        Assert.EndsWith("AcceptVerbsAttribute", found.Where);
        Assert.Empty(reading.Of(Standing.Gone));
    }

    [Fact]
    public void And_a_framework_attribute_is_never_a_dead_package_s_work()
    {
        // The other half of the same defect, and the more expensive one.
        // `UIHint` is System.ComponentModel.DataAnnotations.UIHintAttribute and
        // was counted as ASP.NET MVC's work over 110 uses on nopCommerce,
        // because the exclusion looked for a name the framework spells
        // differently.
        Assert.NotEmpty(FrameworkTypes.Named("UIHint"));
        Assert.Contains(FrameworkTypes.Named("UIHint"),
            full => full.StartsWith("System.", StringComparison.Ordinal));

        Assert.NotEmpty(FrameworkTypes.Named("AttributeUsage"));
    }

    [Fact]
    public void One_direction_only()
    {
        // `Foo` may be the short spelling of `FooAttribute`. `FooAttribute` is
        // never the long spelling of anything else, and looking for
        // `FooAttributeAttribute` would be inventing a name.
        Assert.Equal(
            FrameworkTypes.ByName.TryGetValue("AcceptVerbsAttribute", out var direct) ? direct.Count : 0,
            FrameworkTypes.Named("AcceptVerbsAttribute").Count);
    }

    [Fact]
    public void A_build_that_cannot_read_the_framework_says_so_rather_than_answering()
    {
        // Found by running the desktop build. A single-file publish embeds its
        // assemblies, the framework's own surface came back empty, and the
        // usage surface silently went back to its pre-M13 numbers: 4,379 uses
        // where the server said 3,877, with no error anywhere.
        //
        // The same program has to give the same answer however it was built, or
        // say that it could not look. This test pins the property from the one
        // side a unit test can reach; the packaging side is checked by running
        // the binary and comparing it against the server, which is what found it.
        Assert.True(FrameworkTypes.Readable,
            "this build can read the framework, so the reading must be applicable");

        Assert.True(Read("Microsoft.AspNetCore.Mvc", Use("TagBuilder")).Applicable);
    }
}
