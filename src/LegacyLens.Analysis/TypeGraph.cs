using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LegacyLens.Analysis;

public enum TypeShape { Class, Interface, Record, Struct, Enum, Unknown }

/// <summary>A type declared somewhere in the solution.</summary>
public record TypeInfo(
    string Name,
    string? Namespace,
    TypeShape Shape,
    string Path,
    bool IsAbstract,
    /// <summary>Names written after the colon, before any of them is classified.</summary>
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> Members,
    int Complexity);

public enum RelationKind
{
    /// <summary>B is A's base class.</summary>
    Inherits,
    /// <summary>A implements interface B.</summary>
    Implements,
    /// <summary>A holds a field or property of type B.</summary>
    Holds,
}

public record TypeRelation(string From, string To, RelationKind Kind);

public record TypeMap(
    IReadOnlyList<TypeInfo> Types,
    IReadOnlyList<TypeRelation> Relations,
    /// <summary>Base names that no declaration in the solution accounts for.</summary>
    IReadOnlyList<string> UnresolvedBases);

/// <summary>
/// Extracts types and the relations between them, in two passes.
///
/// The problem this solves: in `class A : B, IC` the compiler knows B is a
/// class and IC an interface, but a syntax tree does not. Guessing from the
/// leading capital I is a convention, not a rule, and legacy code breaks it
/// constantly.
///
/// So the first pass records every type the solution declares and what shape it
/// has. The second pass resolves base lists against that table, and only falls
/// back to the naming convention for types the solution does not define, which
/// are the framework and package types nobody is diagramming anyway.
/// </summary>
public class TypeGraph
{
    public TypeMap Build(IEnumerable<(string Path, string Source)> files)
    {
        var declared = new List<TypeInfo>();

        foreach (var (path, source) in files)
        {
            var walker = new Collector(path);
            walker.Visit(CSharpSyntaxTree.ParseText(source).GetRoot());
            declared.AddRange(walker.Types);
        }

        // First pass complete: the shape of every type the solution declares.
        var shapes = declared
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Shape, StringComparer.Ordinal);

        var relations = new List<TypeRelation>();
        var unresolved = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in declared)
        {
            foreach (var (index, name) in type.BaseTypes.Select((n, i) => (i, n)))
            {
                if (shapes.TryGetValue(name, out var shape))
                {
                    // An interface extending another interface inherits from
                    // it; only a class or struct implements one.
                    var kind = shape == TypeShape.Interface && type.Shape != TypeShape.Interface
                        ? RelationKind.Implements
                        : RelationKind.Inherits;

                    relations.Add(new TypeRelation(type.Name, name, kind));
                    continue;
                }

                unresolved.Add(name);

                // Not declared here: a framework or package type. C# requires
                // the base class first, so anything after position zero is an
                // interface for certain. Position zero falls back to the naming
                // convention, which is all that is left.
                var isInterface = index > 0 || LooksLikeInterface(name);
                relations.Add(new TypeRelation(type.Name, name,
                    isInterface && type.Shape != TypeShape.Interface
                        ? RelationKind.Implements
                        : RelationKind.Inherits));
            }
        }

        return new TypeMap(declared, relations, unresolved.OrderBy(n => n).ToList());
    }

    /// <summary>
    /// The `IFoo` convention. Only consulted for types the solution does not
    /// declare, where nothing better is available.
    /// </summary>
    internal static bool LooksLikeInterface(string name) =>
        name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);

    private sealed class Collector : CSharpSyntaxWalker
    {
        private readonly string _path;
        private string? _namespace;

        public List<TypeInfo> Types { get; } = [];

        public Collector(string path) => _path = path;

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            _namespace = node.Name.ToString();
            base.VisitNamespaceDeclaration(node);
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            _namespace = node.Name.ToString();
            base.VisitFileScopedNamespaceDeclaration(node);
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            Add(node, node.Identifier.Text, TypeShape.Class, node.BaseList, node.Modifiers, node.Members);
            base.VisitClassDeclaration(node);
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            Add(node, node.Identifier.Text, TypeShape.Interface, node.BaseList, node.Modifiers, node.Members);
            base.VisitInterfaceDeclaration(node);
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            Add(node, node.Identifier.Text, TypeShape.Record, node.BaseList, node.Modifiers, node.Members);
            base.VisitRecordDeclaration(node);
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            Add(node, node.Identifier.Text, TypeShape.Struct, node.BaseList, node.Modifiers, node.Members);
            base.VisitStructDeclaration(node);
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            Types.Add(new TypeInfo(node.Identifier.Text, _namespace, TypeShape.Enum, _path,
                false, [], node.Members.Select(m => m.Identifier.Text).ToList(), 0));
            base.VisitEnumDeclaration(node);
        }

        private void Add(
            SyntaxNode node, string name, TypeShape shape, BaseListSyntax? bases,
            SyntaxTokenList modifiers, SyntaxList<MemberDeclarationSyntax> members)
        {
            var baseNames = bases?.Types
                .Select(t => Simplify(t.Type.ToString()))
                .Where(n => n.Length > 0)
                .ToList() ?? [];

            Types.Add(new TypeInfo(
                name,
                _namespace,
                shape,
                _path,
                modifiers.Any(SyntaxKind.AbstractKeyword),
                baseNames,
                PublicMembers(members),
                CountDecisions(node)));
        }

        /// <summary>
        /// Public members only. A diagram showing every private field is a wall
        /// of text; the public surface is what a reader needs to understand how
        /// a type is used.
        /// </summary>
        private static List<string> PublicMembers(SyntaxList<MemberDeclarationSyntax> members)
        {
            var listed = new List<string>();

            foreach (var member in members)
            {
                var modifiers = member switch
                {
                    MethodDeclarationSyntax m => m.Modifiers,
                    PropertyDeclarationSyntax p => p.Modifiers,
                    _ => default,
                };

                if (!modifiers.Any(SyntaxKind.PublicKeyword)) continue;

                listed.Add(member switch
                {
                    MethodDeclarationSyntax m => $"{m.Identifier.Text}()",
                    PropertyDeclarationSyntax p => p.Identifier.Text,
                    _ => string.Empty,
                });
            }

            return listed.Where(m => m.Length > 0).ToList();
        }

        private static int CountDecisions(SyntaxNode node) =>
            node.DescendantNodes().Count(n =>
                n is IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax
                  or ForEachStatementSyntax or CatchClauseSyntax or SwitchSectionSyntax
                  or ConditionalExpressionSyntax);

        /// <summary>
        /// Strips generic arguments and namespace qualification, so that
        /// `System.Collections.Generic.IList&lt;Order&gt;` becomes `IList`.
        /// Diagrams name types, not their instantiations.
        /// </summary>
        private static string Simplify(string written)
        {
            var name = written;

            var generic = name.IndexOf('<');
            if (generic > 0) name = name[..generic];

            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];

            return name.Trim();
        }
    }
}
