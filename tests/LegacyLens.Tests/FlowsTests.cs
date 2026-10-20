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

    private const string Mail = """
        public class Mailer { public void Go() { } public void Ship() { } }
        public class Printer { public void Go() { } }
        public class Order { public void Ship() { } }
        """;

    [Fact]
    public void A_local_of_the_same_name_in_another_branch_is_not_this_one()
    {
        var flow = Trace("Desk.Send", Mail + """
            public class Desk
            {
                public void Send(bool b)
                {
                    if (b) { Mailer x = new Mailer(); x.Go(); }
                    else { var x = new Printer(); x.Go(); }
                }
            }
            """);

        Assert.Equal(["Mailer.Go Shop.cs:1", "Printer.Go Shop.cs:2"], flow.Steps.Select(s => Assert.Single(s.Targets)));
    }

    [Fact]
    public void A_lambda_or_foreach_variable_hides_a_field_of_the_same_name()
    {
        var flow = Trace("Desk.Send", Mail + """
            public class Desk
            {
                Mailer order;
                public void Send(System.Collections.Generic.List<Order> orders)
                {
                    orders.ForEach(order => order.Ship());
                    foreach (var order in orders) order.Ship();
                    foreach (Order typed in orders) typed.Ship();
                }
            }
            """);

        // The two untyped ones are said to be untyped, not taken for the
        // field; the typed foreach resolves to Order, not Mailer.
        Assert.Equal("Order.Ship Shop.cs:3", Assert.Single(Assert.Single(flow.Steps).Targets));
        Assert.Equal(2, flow.Untyped);
    }

    [Fact]
    public void A_pattern_variable_is_typed_by_its_pattern()
    {
        var flow = Trace("Desk.Send", Mail + """
            public class Desk
            {
                public void Send(object o) { if (o is Printer p) p.Go(); }
            }
            """);

        Assert.Equal("Printer.Go Shop.cs:2", Assert.Single(Assert.Single(flow.Steps).Targets));
    }

    [Fact]
    public void An_abstract_or_virtual_member_is_answered_by_its_overrides()
    {
        var flow = Trace("Desk.Save", """
            public abstract class Repo { public abstract void Save(); public virtual void Log() { } public void Plain() { } }
            public class SqlRepo : Repo { public override void Save() { } public override void Log() { } }
            public class MemRepo : Repo { public override void Save() { } }
            public abstract class Lonely { public abstract void Only(); }
            public class OnlyOne : Lonely { public override void Only() { } }
            public abstract class Empty { public abstract void Nothing(); }
            public class Desk
            {
                Repo _repo; Lonely _lonely; Empty _empty;
                public void Save() { _repo.Save(); _repo.Log(); _repo.Plain(); _lonely.Only(); _empty.Nothing(); }
            }
            """);

        var links = flow.Steps.Select(s => (s.Call, s.Link, s.Targets.Count)).ToList();
        Assert.Equal(
        [
            ("Repo.Save", Link.Inferred, 2),          // two overrides
            ("Repo.Log", Link.Inferred, 2),           // the base, or SqlRepo's override
            ("Repo.Plain", Link.Resolved, 1),         // neither abstract nor virtual
            ("Lonely.Only", Link.ThroughInterface, 1),// the only implementation
            ("Empty.Nothing", Link.Unimplemented, 0), // nothing overrides it
        ], links);
    }

    [Fact]
    public void A_conditional_call_is_linked_like_any_other()
    {
        var flow = Trace("Desk.Send", Mail + """
            public class Desk { Mailer _m; dynamic _d; public void Send() { _m?.Go(); _d?.Go(); } }
            """);

        Assert.Equal("Mailer.Go Shop.cs:1", Assert.Single(Assert.Single(flow.Steps).Targets));
        Assert.Equal(1, flow.LeavesSolution + flow.Untyped); // the dynamic one is counted, not dropped
    }
}
