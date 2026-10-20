using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Which tests reach each link of a flow, in the code. Pinned: a test's own
/// calls are followed with the flow's rules; a stub in a test project is not
/// mistaken for a production implementation; a test container's binding
/// decides an interface the way it does when the test runs; and a mocked call
/// reaches nothing.
/// </summary>
public class FlowTestsReachTests
{
    private const string Production = """
        public interface IStock { void Reserve(); }
        public class Stock : IStock { public void Reserve() { } }
        public class CachedStock : IStock { public void Reserve() { } }
        public class Pricing { public int Price() => 1; }
        public class Orders
        {
            IStock _stock; Pricing _pricing;
            public void Place() { _pricing.Price(); _stock.Reserve(); }
        }
        """;

    private static Flow Trace(string tests) =>
        new FlowTracer([("Shop/Shop.cs", Production, false), ("Shop.Tests/Tests.cs", tests, true)]).Trace("Orders.Place")!;

    [Fact]
    public void A_test_that_calls_the_code_reaches_it()
    {
        var flow = Trace("""
            public class PricingTests
            {
                [Fact] public void Prices_are_positive() { var pricing = new Pricing(); pricing.Price(); }
            }
            """);

        var price = flow.Steps.Single(s => s.Call == "Pricing.Price");
        Assert.Equal(["PricingTests.Prices_are_positive Shop.Tests/Tests.cs:3"], price.ReachedBy);
        Assert.Empty(flow.EntryReachedBy);
    }

    [Fact]
    public void A_stub_in_a_test_project_does_not_count_as_an_implementation()
    {
        var flow = new FlowTracer([
            ("Shop/Shop.cs", """
                public interface IStock { void Reserve(); }
                public class Stock : IStock { public void Reserve() { } }
                public class Orders { IStock _stock; public void Place() { _stock.Reserve(); } }
                """, false),
            ("Shop.Tests/Fakes.cs", "public class FakeStock : IStock { public void Reserve() { } }", true),
        ]).Trace("Orders.Place")!;

        var reserve = Assert.Single(flow.Steps);
        Assert.Equal(Link.ThroughInterface, reserve.Link);
        Assert.Equal("Stock.Reserve Shop/Shop.cs:2", Assert.Single(reserve.Targets));
    }

    [Fact]
    public void A_test_containers_binding_decides_the_interface()
    {
        // Two production implementations: from the code alone, a call through
        // IStock is a guess. The test's container says which one, and the
        // test reaches that one and not the other.
        var flow = Trace("""
            public class OrdersTests
            {
                IStock _stock;
                public void Init() { builder.RegisterType<CachedStock>().As<IStock>(); }
                [Test] public void Reserving_works() { _stock.Reserve(); }
            }
            """);

        var reserve = flow.Steps.Single(s => s.Call == "IStock.Reserve");
        Assert.Equal(Link.Inferred, reserve.Link);
        Assert.Equal(["OrdersTests.Reserving_works Shop.Tests/Tests.cs:5"], reserve.ReachedBy);
    }

    [Fact]
    public void A_mocked_call_reaches_nothing()
    {
        var flow = Trace("""
            public class OrdersTests
            {
                [Fact] public void Uses_a_mock() { var mock = new Mock<Pricing>(); mock.Setup(x => x.Price()).Returns(2); }
            }
            """);

        Assert.All(flow.Steps, s => Assert.Empty(s.ReachedBy));
    }

    [Fact]
    public void The_writer_says_reached_and_not_covered()
    {
        var text = FlowWriter.Write(Trace("public class T { [Fact] public void A() { new Pricing().Price(); } }"));

        Assert.Contains("**1 of 2 links are reached by a test**", text);
        Assert.Contains("not proof it is covered when the tests run", text);
        Assert.Contains("no test reaches it", text);
    }
}
