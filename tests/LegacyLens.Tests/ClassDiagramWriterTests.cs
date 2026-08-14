using LegacyLens.Analysis;

namespace LegacyLens.Tests;

public class ClassDiagramWriterTests
{
    private static TypeMap Build(params string[] sources) =>
        new TypeGraph().Build(sources.Select((s, i) => ($"/src/File{i}.cs", s)));

    [Fact]
    public void Inheritance_and_implementation_use_different_arrows()
    {
        var map = Build("namespace N { interface I { } class Base { } class C : Base, I { } }");
        var diagram = new ClassDiagramWriter().ForNamespace(map, "N");

        Assert.Contains("Base <|-- C", diagram);   // solid: inheritance
        Assert.Contains("I <|.. C", diagram);      // dashed: implementation
    }

    [Fact]
    public void Interfaces_abstract_classes_and_records_are_labelled()
    {
        var map = Build("""
            namespace N {
                interface IThing { }
                abstract class Shape { }
                record Point(int X);
            }
            """);
        var diagram = new ClassDiagramWriter().ForNamespace(map, "N");

        Assert.Contains("<<interface>>", diagram);
        Assert.Contains("<<abstract>>", diagram);
        Assert.Contains("<<record>>", diagram);
    }

    [Fact]
    public void A_long_member_list_is_truncated_and_the_rest_counted()
    {
        var members = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"public void M{i}() {{ }}"));
        var map = Build($"namespace N {{ class Big {{ {members} }} }}");

        var diagram = new ClassDiagramWriter { MaxMembers = 4 }.ForNamespace(map, "N");

        Assert.Contains("+M1()", diagram);
        Assert.DoesNotContain("+M12()", diagram);
        Assert.Contains("+8 more", diagram);
    }

    [Fact]
    public void Relations_leaving_the_selection_are_counted_not_drawn()
    {
        // A Mermaid arrow to an undeclared class renders as an empty box, which
        // reads as a type with no name.
        var map = Build("""
            namespace Shown { class A : B { } }
            namespace Elsewhere { class B { } }
            """);

        var diagram = new ClassDiagramWriter().ForNamespace(map, "Shown");

        Assert.DoesNotContain("<|--", diagram);
        Assert.Contains("1 relation(s) to types outside", diagram);
    }

    [Fact]
    public void Around_a_type_pulls_in_one_step_of_neighbours()
    {
        // One step, not the whole chain: following inheritance to its end in old
        // code reaches most of the codebase.
        var map = Build("""
            interface IPlugin { }
            class Shipping : IPlugin { }
            class Payment : IPlugin { }
            class Unrelated { }
            """);

        var diagram = new ClassDiagramWriter().Around(map, "IPlugin");

        Assert.Contains("Shipping", diagram);
        Assert.Contains("Payment", diagram);
        Assert.DoesNotContain("Unrelated", diagram);
    }

    [Fact]
    public void An_empty_selection_says_so_rather_than_returning_nothing()
    {
        var diagram = new ClassDiagramWriter().ForNamespace(Build("class A { }"), "Missing");
        Assert.Contains("nothing to show", diagram);
    }

    [Fact]
    public void Generic_names_do_not_break_the_parser()
    {
        // Angle brackets in a Mermaid class name are a syntax error.
        var map = Build("namespace N { class Repository { } }");
        var diagram = new ClassDiagramWriter().ForNamespace(map, "N");

        var declaration = diagram.Split('\n').Single(l => l.TrimStart().StartsWith("class "));
        Assert.DoesNotContain('<', declaration);
        Assert.DoesNotContain('>', declaration);
    }
}
