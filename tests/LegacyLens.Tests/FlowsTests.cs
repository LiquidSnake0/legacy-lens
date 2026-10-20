using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// A flow is only worth anything if every link in it is right. Each test
/// pins one of the ways a link was got wrong on Orchard before it was fixed:
/// the wrong overload, an extension mistaken for an instance method, a
/// private method offered as an interface's implementation.
/// </summary>
public class FlowsTests
{
    private static Flow Trace(string entry, string source, int depth = 3) =>
        new FlowTracer([("Shop.cs", source)]).Trace(entry, depth)
        ?? throw new InvalidOperationException($"no {entry}");

    private static FlowStep Only(Flow flow) => Assert.Single(flow.Steps);

    [Fact]
    public void An_interface_with_one_implementation_is_followed_into_it()
    {
        var flow = Trace("Orders.Place", """
            public interface IStock { void Reserve(int id); }
            public class Stock : IStock { public void Reserve(int id) { Audit(); } private void Audit() { } }
            public class Orders
            {
                private readonly IStock _stock;
                public void Place() { _stock.Reserve(1); }
            }
            """);

        var step = Only(flow);
        Assert.Equal((Link.ThroughInterface, "IStock.Reserve"), (step.Link, step.Call));
        Assert.Equal("Stock.Reserve Shop.cs:2", Assert.Single(step.Targets));
        Assert.Equal("Stock.Audit", Assert.Single(step.Next).Targets[0].Split(' ')[0]);
    }

    [Fact]
    public void Several_implementations_are_inferred_and_not_entered()
    {
        var flow = Trace("Orders.Place", """
            public interface IStock { void Reserve(); }
            public class Stock : IStock { public void Reserve() { } }
            public class CachedStock : IStock { public void Reserve() { } }
            public class Orders { IStock Stock { get; } public void Place() { Stock.Reserve(); } }
            """);

        var step = Only(flow);
        Assert.Equal(Link.Inferred, step.Link);
        Assert.Equal(2, step.Targets.Count);
        Assert.Empty(step.Next);
    }

    [Fact]
    public void Nothing_implementing_it_is_said_and_a_package_ends_the_branch()
    {
        var flow = Trace("Orders.Place", """
            public interface IStock { void Reserve(); }
            public class Orders
            {
                IStock _stock; System.Collections.Generic.List<int> _ids;
                public void Place() { _stock.Reserve(); _ids.Add(1); }
            }
            """);

        Assert.Equal(Link.Unimplemented, Only(flow).Link);
        Assert.Equal(1, flow.LeavesSolution);
    }

    [Fact]
    public void An_extension_answers_when_the_arguments_do_not_fit_the_instance_method()
    {
        var flow = Trace("Blog.Show", """
            public class Post { }
            public interface IFeeds { void Register(string title, string format, object values); }
            public static class FeedExtensions
            {
                public static void Register(this IFeeds feeds, Post post, string title) { }
            }
            public class Blog { IFeeds _feeds; public void Show() { _feeds.Register(new Post(), "x"); } }
            """);

        Assert.Equal("FeedExtensions.Register Shop.cs:5", Assert.Single(Only(flow).Targets));
    }

    [Fact]
    public void The_overload_is_chosen_by_the_type_of_an_argument()
    {
        var flow = Trace("Blog.Show", """
            public class Route { }
            public class Feeds
            {
                public void Register(string title, Route values) { }
                public void Register(string title, string url) { }
            }
            public class Blog
            {
                Feeds _feeds; string Url { get; }
                public void Show() { _feeds.Register("a", new Route()); _feeds.Register("b", Url); }
            }
            """);

        Assert.Equal(["Feeds.Register Shop.cs:4", "Feeds.Register Shop.cs:5"],
            flow.Steps.Select(s => Assert.Single(s.Targets)));
    }

    [Fact]
    public void A_generic_call_goes_to_the_generic_method()
    {
        var flow = Trace("Blog.Show", """
            public interface IContent { object Get(int id); }
            public static class ContentExtensions { public static T Get<T>(this IContent c, int id) => default; }
            public class Content : IContent { public object Get(int id) => null; }
            public class Blog { IContent _content; public void Show() { _content.Get<string>(1); } }
            """);

        Assert.Equal("ContentExtensions.Get Shop.cs:2", Assert.Single(Only(flow).Targets));
    }

    [Fact]
    public void A_call_through_an_interface_does_not_land_on_a_private_method()
    {
        var flow = Trace("Blog.Show", """
            public interface IQuery { void Slice(int skip); }
            public class Query : IQuery
            {
                private void Slice(int skip) { }
                void IQuery.Slice(int skip) { }
            }
            public class Blog { IQuery _query; public void Show() { _query.Slice(1); } }
            """);

        Assert.Equal("Query.Slice Shop.cs:5", Assert.Single(Only(flow).Targets));
    }

    [Fact]
    public void A_cycle_is_named_once_and_not_entered_again()
    {
        var flow = Trace("A.Ping", """
            public class A { B _b; public void Ping() { _b.Pong(); } }
            public class B { A _a; public void Pong() { _a.Ping(); } }
            """, depth: 6);

        var pong = Only(flow);
        var ping = Assert.Single(pong.Next);
        Assert.Equal("A.Ping", ping.Call);
        Assert.Empty(ping.Next);
    }

    [Fact]
    public void A_call_is_cited_on_the_line_of_its_name()
    {
        var flow = Trace("Blog.Show", """
            public class Query { public Query Where() => this; public void Slice() { } }
            public class Blog
            {
                Query _query;
                public void Show()
                {
                    _query
                        .Slice();
                }
            }
            """);

        Assert.Equal("Shop.cs:8", Only(flow).CalledAt);
    }
}
