using LegacyLens.Analysis;

namespace LegacyLens.Tests;

public class TypeGraphTests
{
    private static TypeMap Build(params string[] sources) =>
        new TypeGraph().Build(sources.Select((s, i) => ($"/src/File{i}.cs", s)));

    private static RelationKind KindOf(TypeMap map, string from, string to) =>
        map.Relations.Single(r => r.From == from && r.To == to).Kind;

    // ---- the two-pass resolution ---------------------------------------

    [Fact]
    public void A_base_declared_as_an_interface_is_an_implementation()
    {
        var map = Build("interface IThing { }", "class Thing : IThing { }");
        Assert.Equal(RelationKind.Implements, KindOf(map, "Thing", "IThing"));
    }

    [Fact]
    public void A_base_declared_as_a_class_is_inheritance()
    {
        var map = Build("class Base { }", "class Derived : Base { }");
        Assert.Equal(RelationKind.Inherits, KindOf(map, "Derived", "Base"));
    }

    [Fact]
    public void A_class_named_like_an_interface_is_still_a_class()
    {
        // The whole reason for the first pass. "Identity" begins with I and a
        // capital, so the naming convention calls it an interface. The
        // declaration says otherwise, and the declaration wins.
        var map = Build("class Identity { }", "class User : Identity { }");
        Assert.Equal(RelationKind.Inherits, KindOf(map, "User", "Identity"));
    }

    [Fact]
    public void An_interface_not_following_the_convention_is_still_an_interface()
    {
        var map = Build("interface Disposable { }", "class Handle : Disposable { }");
        Assert.Equal(RelationKind.Implements, KindOf(map, "Handle", "Disposable"));
    }

    [Fact]
    public void An_interface_extending_another_inherits_rather_than_implements()
    {
        var map = Build("interface IPlugin { }", "interface IShipping : IPlugin { }");
        Assert.Equal(RelationKind.Inherits, KindOf(map, "IShipping", "IPlugin"));
    }

    // ---- types the solution does not declare ----------------------------

    [Fact]
    public void Anything_after_the_first_position_is_an_interface_for_certain()
    {
        // C# requires the base class first, so this holds without knowing what
        // either type is.
        var map = Build("class Controller : SomeBase, ISomething { }");

        Assert.Equal(RelationKind.Inherits, KindOf(map, "Controller", "SomeBase"));
        Assert.Equal(RelationKind.Implements, KindOf(map, "Controller", "ISomething"));
    }

    [Fact]
    public void Undeclared_bases_are_reported_rather_than_hidden()
    {
        // These are framework and package types. Listing them tells the reader
        // where the solution's own graph stops.
        var map = Build("class C : ActionResult { }");
        Assert.Contains("ActionResult", map.UnresolvedBases);
    }

    [Fact]
    public void A_declared_base_is_not_reported_as_unresolved()
    {
        var map = Build("class Base { }", "class Derived : Base { }");
        Assert.Empty(map.UnresolvedBases);
    }

    // ---- extraction -----------------------------------------------------

    [Fact]
    public void Generic_arguments_and_namespaces_are_stripped_from_base_names()
    {
        var map = Build("class Repo : System.Collections.Generic.List<Order> { }");
        Assert.Contains(map.Relations, r => r.To == "List");
    }

    [Fact]
    public void Every_shape_of_type_is_collected()
    {
        var map = Build("class A { } interface I { } record R(int X); struct S { } enum E { One }");

        var shapes = map.Types.ToDictionary(t => t.Name, t => t.Shape);
        Assert.Equal(TypeShape.Class, shapes["A"]);
        Assert.Equal(TypeShape.Interface, shapes["I"]);
        Assert.Equal(TypeShape.Record, shapes["R"]);
        Assert.Equal(TypeShape.Struct, shapes["S"]);
        Assert.Equal(TypeShape.Enum, shapes["E"]);
    }

    [Fact]
    public void Only_public_members_are_listed()
    {
        // A diagram showing every private field is a wall of text.
        var map = Build("""
            class C {
                public int Kept { get; set; }
                private int Hidden { get; set; }
                public void Shown() { }
                void Internal() { }
            }
            """);

        var members = map.Types.Single(t => t.Name == "C").Members;
        Assert.Contains("Kept", members);
        Assert.Contains("Shown()", members);
        Assert.DoesNotContain("Hidden", members);
        Assert.DoesNotContain("Internal()", members);
    }

    [Fact]
    public void The_namespace_is_recorded_in_both_syntaxes()
    {
        var block = Build("namespace Old.Style { class A { } }");
        var scoped = Build("namespace New.Style;\nclass B { }");

        Assert.Equal("Old.Style", block.Types.Single().Namespace);
        Assert.Equal("New.Style", scoped.Types.Single().Namespace);
    }

    [Fact]
    public void An_abstract_class_is_marked()
    {
        var map = Build("abstract class Shape { } class Circle : Shape { }");
        Assert.True(map.Types.Single(t => t.Name == "Shape").IsAbstract);
        Assert.False(map.Types.Single(t => t.Name == "Circle").IsAbstract);
    }

    [Fact]
    public void A_file_that_does_not_parse_does_not_abort_the_others()
    {
        var map = Build("class Broken { void M( {", "class Fine { }");
        Assert.Contains(map.Types, t => t.Name == "Fine");
    }
}
