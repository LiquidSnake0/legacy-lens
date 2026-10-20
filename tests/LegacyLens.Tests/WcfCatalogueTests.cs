using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// WCF is read like a package although it is part of the .NET Framework, and
/// the catalogue answers it with CoreWCF on the server side. What is pinned
/// here is the answer for the two halves a WCF service has: the contract, which
/// carries over by name, and the host, which does not.
/// </summary>
public class WcfCatalogueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lens-wcf-{Guid.NewGuid():N}");

    public WcfCatalogueTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Orders.csproj"), """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Orders.cs"), """
            using System.ServiceModel;

            [ServiceContract]
            public interface IOrders
            {
                [OperationContract]
                [FaultContract(typeof(string))]
                void Place();
            }

            public class Program
            {
                public static void Main()
                {
                    var host = new ServiceHost(typeof(object));
                    host.AddServiceEndpoint(typeof(IOrders), new BasicHttpBinding(), "orders");
                }
            }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Wcf_is_answered_by_corewcf_with_the_host_left_as_a_change()
    {
        var wcf = Assert.Single(new Assessor().Assess(_root).Uses, d => d.Surface.Package == "System.ServiceModel");
        var best = Assert.IsType<Coverage>(wcf.Best);

        Assert.Equal("CoreWCF.Primitives", best.Candidate);
        Assert.Contains(best.Covered, t => t.Name == "ServiceContract");
        Assert.Contains(best.Covered, t => t.Name == "BasicHttpBinding");
        Assert.Contains(best.Unavailable, t => t.Name == "ServiceHost");
    }
}
