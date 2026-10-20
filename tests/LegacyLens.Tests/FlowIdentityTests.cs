using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Two things that decide whether a flow can be trusted on a real solution:
/// that two types sharing a name stay two types, as forty AdminControllers do
/// in Orchard, and that a generated test which ran green marks only the member
/// it calls directly.
/// </summary>
public class FlowIdentityTests
{
    private static readonly (string, string, bool)[] TwoModules =
    [
        ("Blogs/AdminController.cs", """
            using Shop.Blogs.Services;
            namespace Shop.Blogs.Controllers
            {
                public class AdminController { IPosts _posts; public void Create() { _posts.Add(); } }
            }
            namespace Shop.Blogs.Services
            {
                public interface IPosts { void Add(); }
                public class Posts : IPosts { public void Add() { } }
            }
            """, false),
        ("Users/AdminController.cs", """
            using Shop.Users.Services;
            namespace Shop.Users.Controllers
            {
                public class AdminController { IAccounts _posts; public void Create() { _posts.Open(); } }
            }
            namespace Shop.Users.Services
            {
                public interface IAccounts { void Open(); }
                public class Accounts : IAccounts { public void Open() { } }
            }
            """, false),
    ];

    [Fact]
    public void Two_types_of_one_name_stay_two_and_the_short_name_is_ambiguous()
    {
        var tracer = new FlowTracer(TwoModules);

        Assert.Null(tracer.Trace("AdminController.Create"));
        Assert.Equal(["Shop.Blogs.Controllers.AdminController.Create", "Shop.Users.Controllers.AdminController.Create"],
            tracer.Candidates("AdminController.Create").Order());

        // Each controller's field is typed in its own file's scope: the same
        // field name, a different service.
        var users = tracer.Trace("Shop.Users.Controllers.AdminController.Create")!;
        Assert.Equal("Accounts.Open Users/AdminController.cs:9", Assert.Single(Assert.Single(users.Steps).Targets));
    }

    [Fact]
    public void A_generated_test_that_passed_records_the_member_it_calls_and_nothing_below()
    {
        var tracer = new FlowTracer(
            [("Shop/Shop.cs", """
                namespace Shop
                {
                    public static class Names { public static bool Valid(string s) => Clean(s) != ""; static string Clean(string s) => s.Trim(); }
                    public class Controller { public void Create(string id) { if (Names.Valid(id)) { } } }
                }
                """, false)],
            [("NamesCharacterization.cs", """
                public class NamesCharacterization
                {
                    [Fact] public void Valid_1() { var result = global::Shop.Names.Valid(""); }
                }
                """)]);

        var valid = Assert.Single(tracer.Trace("Controller.Create")!.Steps);

        Assert.Equal(["NamesCharacterization.Valid_1 NamesCharacterization.cs:3"], valid.RecordedBy);
        Assert.Empty(valid.ReachedBy); // a generated test is not counted twice
        Assert.Empty(Assert.Single(valid.Next).RecordedBy); // Clean ran, but nothing proves which branch
    }

    [Fact]
    public void Nested_types_of_one_name_and_a_generic_and_its_namesake_stay_apart()
    {
        var tracer = new FlowTracer([("a.cs", """
            namespace N
            {
                public class A { public class Options { public void Apply() { } } }
                public class B { public class Options { public void Apply() { } } }
                public class Repository { public void Get(int id) { } }
                public class Repository<T> { public void Get(int id) { } }
                public class Desk
                {
                    B.Options _options; Repository<string> _typed; Repository _plain;
                    public void Run() { _options.Apply(); _typed.Get(1); _plain.Get(1); }
                }
            }
            """, false)]);

        var steps = tracer.Trace("Desk.Run")!.Steps;

        Assert.Equal(["Options.Apply a.cs:4", "Repository.Get a.cs:6", "Repository.Get a.cs:5"],
            steps.Select(s => Assert.Single(s.Targets)));
        Assert.All(steps, s => Assert.Equal(Link.Resolved, s.Link));
        Assert.Equal(["N.Repository.Get", "N.Repository`1.Get"], tracer.Candidates("Repository.Get").Order());
    }

    [Fact]
    public void Each_part_of_a_partial_class_resolves_with_its_own_files_usings()
    {
        var tracer = new FlowTracer(
        [
            ("one.cs", "namespace One { public class Store { public void Save() { } } }", false),
            ("two.cs", "namespace Two { public class Store { public void Save() { } } }", false),
            ("b.cs", "using One; namespace App { public partial class Shop { Store _first; public void A() { _first.Save(); } } }", false),
            ("c.cs", "using Two; namespace App { public partial class Shop { Store _second; public void B() { _second.Save(); } } }", false),
        ]);

        Assert.Equal("Store.Save one.cs:1", Assert.Single(Assert.Single(tracer.Trace("Shop.A")!.Steps).Targets));
        Assert.Equal("Store.Save two.cs:1", Assert.Single(Assert.Single(tracer.Trace("Shop.B")!.Steps).Targets));
    }
}
