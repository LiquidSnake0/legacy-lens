using System.Text;

namespace LegacyLens.Analysis;

/// <summary>
/// Renders part of a type graph as a Mermaid class diagram.
///
/// Part, never all of it. nopCommerce declares thousands of types; a diagram of
/// all of them is a grey rectangle. Every method here takes a way of choosing
/// what to show, and states what it left out.
/// </summary>
public class ClassDiagramWriter
{
    /// <summary>Members listed per type before the rest are summarised.</summary>
    public int MaxMembers { get; init; } = 8;

    /// <summary>
    /// Types in one namespace, with the relations between them.
    /// </summary>
    public string ForNamespace(TypeMap map, string @namespace)
    {
        var selected = map.Types
            .Where(t => string.Equals(t.Namespace, @namespace, StringComparison.Ordinal))
            .ToList();

        return Render(map, selected, $"namespace {@namespace}");
    }

    /// <summary>
    /// One type and everything one step away from it: what it inherits, what it
    /// implements, and what inherits from it.
    ///
    /// One step, not all of them. Following the graph to its end on an
    /// inheritance chain in old code reaches most of the codebase.
    /// </summary>
    public string Around(TypeMap map, string typeName)
    {
        var neighbours = map.Relations
            .Where(r => r.From == typeName || r.To == typeName)
            .SelectMany(r => new[] { r.From, r.To })
            .ToHashSet(StringComparer.Ordinal);

        neighbours.Add(typeName);

        var selected = map.Types.Where(t => neighbours.Contains(t.Name)).ToList();
        return Render(map, selected, $"around {typeName}");
    }

    private string Render(TypeMap map, IReadOnlyList<TypeInfo> selected, string subject)
    {
        if (selected.Count == 0)
        {
            return $"classDiagram\n  %% nothing to show for {subject}\n";
        }

        var names = selected.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var builder = new StringBuilder("classDiagram\n");

        foreach (var type in selected.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            builder.AppendLine($"  class {Identifier(type.Name)} {{");

            if (type.Shape is TypeShape.Interface or TypeShape.Enum or TypeShape.Record)
            {
                builder.AppendLine($"    <<{type.Shape.ToString().ToLowerInvariant()}>>");
            }
            else if (type.IsAbstract)
            {
                builder.AppendLine("    <<abstract>>");
            }

            foreach (var member in type.Members.Take(MaxMembers))
            {
                builder.AppendLine($"    +{Escape(member)}");
            }

            if (type.Members.Count > MaxMembers)
            {
                builder.AppendLine($"    +{type.Members.Count - MaxMembers} more");
            }

            builder.AppendLine("  }");
        }

        builder.AppendLine();

        // Relations to types outside the selection are dropped: a Mermaid arrow
        // to an undeclared class renders as an empty box, which reads as a type
        // with no name.
        var drawn = map.Relations
            .Where(r => names.Contains(r.From) && names.Contains(r.To))
            .Distinct()
            .ToList();

        foreach (var relation in drawn)
        {
            var arrow = relation.Kind switch
            {
                RelationKind.Inherits => "<|--",
                RelationKind.Implements => "<|..",
                _ => "-->",
            };
            builder.AppendLine($"  {Identifier(relation.To)} {arrow} {Identifier(relation.From)}");
        }

        var outside = map.Relations.Count(r => names.Contains(r.From) && !names.Contains(r.To));
        if (outside > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"  %% {outside} relation(s) to types outside {subject} not drawn");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Mermaid class names tolerate less than node ids do. Generic markers and
    /// dots break the parser outright.
    /// </summary>
    private static string Identifier(string name)
    {
        var safe = new StringBuilder();
        foreach (var character in name)
        {
            safe.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }
        return safe.Length == 0 ? "_" : safe.ToString();
    }

    private static string Escape(string member) => member.Replace("~", "").Replace("<", "").Replace(">", "");
}
