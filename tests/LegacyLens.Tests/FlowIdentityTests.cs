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
}
