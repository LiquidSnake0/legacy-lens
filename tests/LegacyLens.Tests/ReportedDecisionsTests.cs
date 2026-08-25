using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// What only a person can settle, in the document that person keeps.
///
/// The conversions have always said these, in a terminal, one line per project.
/// The report carried an ordered plan and none of the decisions the plan
/// depends on: nothing about the target the whole solution has to move to
/// before PackageReference works, nothing about the keys the code reads that
/// nothing declares. **A decision made in a terminal and not written down is a
/// decision nobody made.**
/// </summary>
public class ReportedDecisionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-decide-{Guid.NewGuid():N}");

    public ReportedDecisionsTests()
    {
        var app = Path.Combine(_root, "App");
        Directory.CreateDirectory(app);

        File.WriteAllText(Path.Combine(app, "App.csproj"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="12.0" DefaultTargets="Build"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFrameworkVersion>v4.5.1</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Newtonsoft.Json">
                  <HintPath>..\packages\Newtonsoft.Json.11.0.2\lib\net45\Newtonsoft.Json.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(app, "packages.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Newtonsoft.Json" version="11.0.2" targetFramework="net451" />
            </packages>
            """);

        File.WriteAllText(Path.Combine(app, "web.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="Mail.Host" value="smtp.example.com" />
              </appSettings>
            </configuration>
            """);

        // Read by the code and declared nowhere, which is a decision: no value
        // can be invented for it.
        File.WriteAllText(Path.Combine(app, "Thing.cs"), """
            using System.Configuration;

            public class Thing
            {
                public string Missing() => ConfigurationManager.AppSettings["Nobody.Declared.This"];
            }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Assessment Assess() => new Assessor().Assess(_root);

    private string Report() => System.Text.RegularExpressions.Regex.Replace(
        new ReportWriter().Write(Assess()), @"\s+", " ");

    [Fact]
    public void The_assessment_carries_what_the_conversions_could_not_settle()
    {
        var decisions = Assess().ToDecide;

        Assert.Contains(decisions, one => one.What.About == "target-below-461");
        Assert.Contains(decisions, one => one.What.About == "undeclared-keys");
    }

    [Fact]
    public void And_carries_only_those()
    {
        // A consequence is something the conversion did and the reader checks.
        // Folding it in here would put the decisions back where they were,
        // which is buried.
        Assert.All(Assess().ToDecide, one => Assert.True(one.What.Decides));

        Assert.DoesNotContain(Assess().ToDecide, one => one.What.About == "binding-redirects");
    }

    [Fact]
    public void The_document_says_them_and_says_whose_they_are()
    {
        var report = Report();

        Assert.Contains("What only you can decide", report);
        Assert.Contains("PackageReference is supported from 4.6.1", report);
        Assert.Contains("none of it can be settled by reading the code harder", report);
    }

    [Fact]
    public void They_come_before_the_order_that_assumes_them()
    {
        var report = Report();

        var decisions = report.IndexOf("What only you can decide", StringComparison.Ordinal);
        var order = report.IndexOf("In what order", StringComparison.Ordinal);

        Assert.True(decisions >= 0 && order >= 0);
        Assert.True(decisions < order, "you decide, then you sequence");
    }

    [Fact]
    public void A_solution_with_nothing_to_settle_gets_no_section()
    {
        // A heading over an empty list reads as a finding, and this one would
        // read as "there is nothing to decide", which is never true and would
        // be the worst thing this section could say.
        var empty = Path.Combine(_root, "nothing");
        Directory.CreateDirectory(empty);

        var report = new ReportWriter().Write(new Assessor().Assess(empty));

        Assert.DoesNotContain("What only you can decide", report);
    }
}
