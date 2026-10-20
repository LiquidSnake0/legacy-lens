using LegacyLens.Api.Mcp;

namespace LegacyLens.Tests;

/// <summary>
/// What a coding agent can get out of the MCP server, and what it cannot.
///
/// The tools answer with the report's own sections, so these check that the
/// words are the report's, and that the two tools that read files read only
/// inside the codebase the server was started on. An agent names paths it read
/// in someone else's repository; the server is what stands between that text
/// and the rest of the disk.
/// </summary>
public class McpToolsTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), $"lens-mcp-{Guid.NewGuid():N}");
    private readonly string _root;

    public McpToolsTests()
    {
        _root = Path.Combine(_parent, "shop");
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "App.csproj"), """
            <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
                <OutputType>Library</OutputType>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Home.cs"), """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index() => View();
            }
            """);
        File.WriteAllText(Path.Combine(_parent, "secret.txt"), "not yours");
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true);
        GC.SuppressFinalize(this);
    }

    private LensTools Tools() => new(new Codebase(_root));

    [Fact]
    public void The_summary_is_the_reports_own_opening()
    {
        var summary = Tools().Summary();

        Assert.StartsWith("## At a glance", summary);
        Assert.Contains("## In short", summary);
        Assert.DoesNotContain("## What will hurt", summary);
    }

    [Fact]
    public void The_limits_are_an_answer_of_their_own()
    {
        // An agent that reads only the summary would act on it. The limits get
        // their own tool, and the tool returns the report's section whole.
        var limits = Tools().Limitations();

        Assert.StartsWith("## What this report does not say", limits);
        Assert.Contains("Nothing was compiled.", limits);
    }

    [Fact]
    public void Lines_are_numbered_so_a_citation_can_be_checked()
    {
        var lines = Tools().ReadLines("Home.cs", 3, 4);

        Assert.Contains("     3  public class HomeController : Controller", lines);
        Assert.Contains("     4  {", lines);
        Assert.DoesNotContain("using System.Web.Mvc", lines);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/etc/passwd")]
    public void Nothing_outside_the_codebase_is_read(string path)
    {
        Assert.StartsWith("Refused:", Tools().ReadLines(path));
    }

    [Fact]
    public void Nor_through_a_link_that_points_outside_it()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "innocent.cs"), Path.Combine(_parent, "secret.txt"));

        Assert.StartsWith("Refused:", Tools().ReadLines("innocent.cs"));
        Assert.DoesNotContain("not yours", Tools().Search("not yours", ".cs"));
    }

    [Fact]
    public void A_search_answers_with_file_and_line()
    {
        Assert.Equal("Home.cs:3: public class HomeController : Controller",
            Tools().Search("class HomeController").Trim());
    }

    [Fact]
    public void A_bad_pattern_is_explained_rather_than_thrown()
    {
        Assert.StartsWith("Not a valid regular expression", Tools().Search("(unclosed"));
    }

    [Fact]
    public void A_package_the_code_does_not_use_names_the_ones_it_does()
    {
        var answer = Tools().PackageUsage("Newtonsoft.Json");

        Assert.StartsWith("Nothing in this codebase uses Newtonsoft.Json", answer);
        Assert.Contains("Microsoft.AspNet.Mvc", answer);
    }

    [Fact]
    public void A_package_it_does_use_comes_with_every_type()
    {
        var answer = Tools().PackageUsage("microsoft.aspnet.mvc");

        Assert.Contains("| Controller |", answer);
        Assert.Contains("| ActionResult |", answer);
    }

    [Fact]
    public void Entry_points_come_with_file_and_line()
    {
        var entries = Tools().EntryPoints();

        Assert.Contains("MvcAction HomeController.Index Home.cs:5", entries);
        Assert.Equal("No entry point matches.", Tools().EntryPoints(kind: "WcfOperation"));
    }
}
