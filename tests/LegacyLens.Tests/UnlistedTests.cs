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
    public void A_type_the_base_library_still_provides_is_not_work()
    {
        // Measured on Orchard: 69 of the 219 types the catalogue never mentions
        // are these, over 502 calls. They were being counted as migration work
        // because they appeared in a file that imported a dead package.
        var reading = Read("Microsoft.AspNetCore.Mvc",
            Use("TextWriter"), Use("ArgumentException"), Use("Lazy"));

        Assert.Equal(3, reading.Of(Standing.Unchanged).Count);
        Assert.Equal(0, reading.Left);

        Assert.Equal("System.IO.TextWriter",
            reading.Of(Standing.Unchanged).Single(t => t.Use.Name == "TextWriter").Where);
    }

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
            Use("TextWriter"), Use("TagBuilder"), Use("HttpContext"), Use("HttpUnauthorizedResult"));

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
    public void The_two_survivors_of_System_Web_count_as_still_there()
    {
        // An earlier version excluded System.* names under System.Web, on the
        // assumption that the whole family went away with ASP.NET. Modern .NET
        // keeps exactly two, and Orchard uses IHtmlString more than a hundred
        // times, so the exclusion reported two survivors as losses.
        var reading = Read("Microsoft.AspNetCore.Mvc", Use("HttpUtility"), Use("IHtmlString"));

        Assert.Equal(2, reading.Of(Standing.Unchanged).Count);
        Assert.Equal(0, reading.Left);
        Assert.All(reading.Types, t => Assert.StartsWith("System.Web.", t.Where));
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
        Assert.Empty(reading.Of(Standing.Unchanged));
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

        Read("Microsoft.AspNetCore.Mvc", Use("TagBuilder"));

        var after = Successors.Load().For("Microsoft.AspNet.Mvc").FirstOrDefault();

        Assert.Equal(before?.Types.Count, after?.Types.Count);
        Assert.False(after?.Types.ContainsKey("TagBuilder"));
    }

    [Fact]
    public void An_empty_column_reads_as_empty_rather_than_as_settled()
    {
        var reading = Read("Microsoft.AspNetCore.Mvc");

        Assert.Empty(reading.Types);
        Assert.Equal(0, reading.Left);
    }
}
