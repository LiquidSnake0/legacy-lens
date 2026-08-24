using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// A seam is a place where behaviour can be changed without editing the code
/// around it. What matters most here is the refusal: listing the interfaces in
/// a solution is easy, and saying plainly that a type cannot be cut is the part
/// that saves the three weeks spent finding out by hand.
/// </summary>
public class SeamsTests
{
    private static SeamSurvey Survey(params string[] sources)
    {
        var files = sources.Select((s, i) => ($"File{i}.cs", s)).ToList();
        return new Seams().Find(files, new TypeGraph().Build(files));
    }

    private static TypeSeams Only(SeamSurvey survey, string name) =>
        survey.Types.Single(t => t.Name == name);

    [Fact]
    public void A_type_behind_an_interface_that_reaches_nothing_can_be_swapped_today()
    {
        var survey = Survey(
            "public interface IClock { int Hour(); }",
            "public class Fixed : IClock { public int Hour() => 9; }");

        Assert.Equal(SeamVerdict.Substitutable, Only(survey, "Fixed").Verdict);
    }

    [Fact]
    public void A_plain_type_with_no_interface_needs_one_extracted()
    {
        var survey = Survey("public class Calculator { public int Twice(int n) => n * 2; }");

        var seam = Only(survey, "Calculator");
        Assert.Equal(SeamVerdict.AfterExtraction, seam.Verdict);
        Assert.Contains("Extracting an interface is enough", seam.Reason);
    }

    [Fact]
    public void A_static_class_has_no_instance_to_substitute()
    {
        var survey = Survey("public static class Helpers { public static int Twice(int n) => n * 2; }");

        var seam = Only(survey, "Helpers");
        Assert.Equal(SeamVerdict.NotWithoutRewrite, seam.Verdict);
        Assert.Contains("no instance", seam.Reason);
    }

    [Theory]
    [InlineData("public class Stamped { public object At() => System.DateTime.Now; }", "DateTime.Now")]
    [InlineData("public class Reader { public string Read() => File.ReadAllText(\"a\"); }", "File")]
    [InlineData("public class Ids { public object Next() => Guid.NewGuid(); }", "Guid.NewGuid")]
    [InlineData("public class Web { public object Ctx() => HttpContext.Current; }", "HttpContext.Current")]
    public void An_ambient_call_closes_the_seam(string source, string expected)
    {
        var seam = Survey(source).Types.Single();

        Assert.Equal(SeamVerdict.NotWithoutRewrite, seam.Verdict);
        Assert.Contains(seam.Ambients, a => a.Name == expected);
        Assert.Contains("passed in", seam.Reason);
    }

    [Fact]
    public void Constructing_a_connection_is_the_dependency()
    {
        var seam = Survey(
            "public class Repo { public object Open() => new SqlConnection(\"cs\"); }").Types.Single();

        Assert.Equal(SeamVerdict.NotWithoutRewrite, seam.Verdict);
        Assert.Contains(seam.Ambients, a => a.Name == "new SqlConnection");
    }

    [Fact]
    public void An_interface_is_not_something_to_substitute()
    {
        var survey = Survey("public interface IThing { void Do(); }");

        Assert.Empty(survey.Types);
    }

    [Fact]
    public void A_sealed_type_with_nothing_overridable_and_no_interface_cannot_be_stood_in_for()
    {
        var seam = Survey("public sealed class Locked { public int Value => 1; }").Types.Single();

        Assert.Equal(SeamVerdict.NotWithoutRewrite, seam.Verdict);
        Assert.Contains("Nothing can stand in for it", seam.Reason);
    }

    [Fact]
    public void A_sealed_type_behind_an_interface_is_still_substitutable()
    {
        var survey = Survey(
            "public interface IClock { int Hour(); }",
            "public sealed class Fixed : IClock { public int Hour() => 9; }");

        Assert.Equal(SeamVerdict.Substitutable, Only(survey, "Fixed").Verdict);
    }

    [Fact]
    public void Repeated_calls_are_counted_rather_than_listed_twice()
    {
        var seam = Survey(
            "public class Twice { public object A() => System.DateTime.Now; " +
            "public object B() => System.DateTime.Now; }").Types.Single();

        Assert.Equal(2, Assert.Single(seam.Ambients).Uses);
    }

    [Fact]
    public void What_closes_the_most_seams_comes_first()
    {
        var survey = Survey(
            "public class A { public string R() => File.ReadAllText(\"a\"); }",
            "public class B { public string R() => File.ReadAllText(\"b\"); }",
            "public class C { public object N() => Guid.NewGuid(); }");

        Assert.Equal("File", survey.ClosedBy[0].Name);
        Assert.Equal(2, survey.ClosedBy[0].Types);
    }
}
