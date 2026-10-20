using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Where a codebase is entered. Each kind is pinned on the smallest source that
/// has it, with the rule that decides it, because an entry point the list
/// misses is a flow nobody will trace.
/// </summary>
public class EntryPointsTests
{
    private static IReadOnlyList<EntryPoint> Read(params (string Path, string Source)[] files) =>
        new EntryPointReader().Read(files.Select(f => ("Shop", f.Path, f.Source)));

    [Fact]
    public void An_action_is_found_through_an_in_house_base_controller()
    {
        var entries = Read(("Base.cs", """
            public abstract class AdminControllerBase : System.Web.Mvc.Controller
            {
                public string Shared() => "";
            }
            """), ("Orders.cs", """
            public class OrdersController : AdminControllerBase
            {
                [HttpPost]
                public ActionResult Place() => null;

                public ActionResult List() => null;

                [NonAction]
                public void Helper() { }

                public static void Utility() { }

                protected override void OnException(object context) { }
            }
            """));

        // The abstract base is entered through each concrete controller, not
        // on its own; a NonAction, a static and an override are not actions.
        Assert.Equal(["Place", "List"], entries.Select(e => e.Member));
        Assert.All(entries, e => Assert.Equal(EntryKind.MvcAction, e.Kind));
        Assert.Equal("HttpPost", entries[0].Detail);
        Assert.Equal(("Orders.cs", 4), (entries[0].Path, entries[0].Line));
    }

    [Fact]
    public void A_wcf_operation_is_found_on_the_class_implementing_the_contract()
    {
        var entries = Read(("Orders.cs", """
            [ServiceContract]
            public interface IOrders
            {
                [OperationContract] void Place();
                void NotExposed();
            }

            public class OrderService : IOrders
            {
                public void Place() { }
                public void NotExposed() { }
            }
            """));

        var operation = Assert.Single(entries);
        Assert.Equal((EntryKind.WcfOperation, "OrderService", "Place", "IOrders"),
            (operation.Kind, operation.Type, operation.Member, operation.Detail));
    }

    [Theory]
    [InlineData("public class Feed : System.Web.Services.WebService { [WebMethod] public string Latest() => \"\"; }", EntryKind.AsmxMethod, "Latest")]
    [InlineData("public class Global : System.Web.HttpApplication { protected void Application_Start() { } }", EntryKind.ApplicationEvent, "Application_Start")]
    [InlineData("public class Nightly : Quartz.IJob { public void Execute(object c) { } }", EntryKind.BackgroundTask, "")]
    [InlineData("public class Thumbs : IHttpHandler { public void ProcessRequest(object c) { } }", EntryKind.HttpHandler, "")]
    [InlineData("public class Cli { [CommandName(\"blog create\")] public void Create() { } }", EntryKind.Command, "Create")]
    [InlineData("public class MetaWeblog : IXmlRpcHandler { }", EntryKind.RemoteProcedure, "")]
    [InlineData("public class Startup { public void Configuration(IAppBuilder app) { } }", EntryKind.OwinStartup, "Configuration")]
    [InlineData("static class Program { static void Main(string[] args) { } }", EntryKind.ProgramMain, "Main")]
    public void Each_kind_is_found_by_its_own_rule(string source, EntryKind kind, string member)
    {
        var entry = Assert.Single(Read(("File.cs", source)));

        Assert.Equal((kind, member), (entry.Kind, entry.Member));
    }

    [Fact]
    public void A_test_project_is_not_entered()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lens-entries-{Guid.NewGuid():N}");
        try
        {
            foreach (var (name, reference) in new[] { ("Shop.Web", ""), ("Shop.Tests", "<Reference Include=\"nunit.framework\" />") })
            {
                var folder = Path.Combine(root, name);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"{name}.csproj"), $"""
                    <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                      <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup>
                      <ItemGroup>{reference}</ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(folder, "HomeController.cs"),
                    "public class HomeController : Controller { public object Index() => null; }");
            }

            var entries = EntryPointReader.ReadSolution(root, new ProjectGraph().Build(root));

            var entry = Assert.Single(entries);
            Assert.Equal(("Shop.Web", "Shop.Web/HomeController.cs"), (entry.Project, entry.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
