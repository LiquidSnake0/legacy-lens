using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LegacyLens.Analysis;

/// <summary>Whether a type can be replaced, and at what cost.</summary>
public enum SeamVerdict
{
    /// <summary>It sits behind an interface and reaches nothing ambient. Swap it today.</summary>
    Substitutable,

    /// <summary>Nothing ambient holds it, but it has no interface yet. Extract one.</summary>
    AfterExtraction,

    /// <summary>There is nowhere to cut. Say so rather than promise an increment.</summary>
    NotWithoutRewrite,
}

/// <summary>A call that reaches out of the method to something nobody passed in.</summary>
public record Ambient(string Name, int Uses);

public record TypeSeams(
    string Name,
    string Path,
    bool ImplementsInterface,
    bool IsStatic,
    bool IsSealed,
    /// <summary>Members a subclass could replace: virtual or abstract.</summary>
    int Overridable,
    IReadOnlyList<Ambient> Ambients)
{
    public int AmbientUses => Ambients.Sum(a => a.Uses);

    public SeamVerdict Verdict =>
        IsStatic ? SeamVerdict.NotWithoutRewrite
        : Ambients.Count > 0 ? SeamVerdict.NotWithoutRewrite
        : ImplementsInterface ? SeamVerdict.Substitutable
        : IsSealed && Overridable == 0 ? SeamVerdict.NotWithoutRewrite
        : SeamVerdict.AfterExtraction;

    /// <summary>Why, in the words a person would use to explain the decision.</summary>
    public string Reason =>
        IsStatic
            ? "A static class has no instance, so there is nothing to substitute."
        : Ambients.Count > 0
            ? $"Reaches {string.Join(", ", Ambients.Select(a => a.Name))} directly. " +
              "Those calls have to be passed in before anything can replace them."
        : ImplementsInterface
            ? "Already behind an interface and reaches nothing ambient."
        : IsSealed && Overridable == 0
            ? "Sealed, with no overridable member and no interface. Nothing can stand in for it."
            : "Nothing ambient holds it. Extracting an interface is enough.";
}

public record SeamSurvey(IReadOnlyList<TypeSeams> Types)
{
    public int Total => Types.Count;
    public int Substitutable => Types.Count(t => t.Verdict == SeamVerdict.Substitutable);
    public int AfterExtraction => Types.Count(t => t.Verdict == SeamVerdict.AfterExtraction);
    public int NotWithoutRewrite => Types.Count(t => t.Verdict == SeamVerdict.NotWithoutRewrite);

    /// <summary>
    /// The ambient dependencies that close the most seams. Four names holding
    /// half an estate is a different plan from forty holding one each.
    /// </summary>
    public IReadOnlyList<(string Name, int Types)> ClosedBy =>
        Types.SelectMany(t => t.Ambients.Select(a => a.Name))
            .GroupBy(name => name)
            .Select(g => (Name: g.Key, Types: g.Count()))
            .OrderByDescending(x => x.Types)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// Where the code can be cut, and where it cannot.
///
/// A strangler fig needs somewhere to put the new implementation beside the
/// old. Michael Feathers calls that a seam: a place where behaviour can be
/// changed without editing the code around it. This finds the ones that exist,
/// and the calls that close them.
///
/// The refusals are the point. Anyone can list the interfaces in a solution.
/// Saying plainly that a type cannot be cut, and why, is what saves the three
/// weeks spent discovering it by hand.
/// </summary>
public class Seams
{
    /// <summary>
    /// Static members that reach outside the method for something nobody
    /// passed in: the clock, the disk, the request, the environment.
    ///
    /// Named rather than inferred. A heuristic here produces a confident list
    /// that is wrong, and the whole argument for this tool is that it reports
    /// what it read.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AmbientMembers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DateTime.Now"] = "the clock",
            ["DateTime.UtcNow"] = "the clock",
            ["DateTime.Today"] = "the clock",
            ["DateTimeOffset.Now"] = "the clock",
            ["DateTimeOffset.UtcNow"] = "the clock",
            ["Guid.NewGuid"] = "randomness",
            ["HttpContext.Current"] = "the request",
            ["ConfigurationManager.AppSettings"] = "configuration",
            ["ConfigurationManager.ConnectionStrings"] = "configuration",
            ["Environment.CurrentDirectory"] = "the environment",
            ["Environment.MachineName"] = "the environment",
            ["Environment.GetEnvironmentVariable"] = "the environment",
            ["Thread.Sleep"] = "the clock",
        };

    /// <summary>Static types whose every member reaches the disk or the console.</summary>
    private static readonly IReadOnlySet<string> AmbientTypes =
        new HashSet<string>(StringComparer.Ordinal) { "File", "Directory", "Console" };

    /// <summary>
    /// Types whose construction is the dependency. `new SqlConnection(...)` in
    /// a method body is a database nobody can substitute.
    /// </summary>
    private static readonly IReadOnlySet<string> AmbientConstructions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "SqlConnection", "SqlCommand", "OleDbConnection", "HttpClient",
            "WebClient", "StreamReader", "StreamWriter", "FileStream", "Random",
        };

    public SeamSurvey Find(IEnumerable<(string Path, string Source)> files, TypeMap map)
    {
        var behindInterface = map.Relations
            .Where(r => r.Kind == RelationKind.Implements)
            .Select(r => r.From)
            .ToHashSet(StringComparer.Ordinal);

        var found = new List<TypeSeams>();

        foreach (var (path, source) in files)
        {
            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(source).GetRoot();
            }
            catch (Exception)
            {
                // A file that will not parse is not a type with no seams.
                continue;
            }

            foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (declaration is InterfaceDeclarationSyntax) continue;

                var name = declaration.Identifier.Text;
                var modifiers = declaration.Modifiers.Select(m => m.Text).ToHashSet(StringComparer.Ordinal);

                var overridable = declaration.Members
                    .OfType<MemberDeclarationSyntax>()
                    .Count(m => m.Modifiers.Any(t =>
                        t.IsKind(SyntaxKind.VirtualKeyword) || t.IsKind(SyntaxKind.AbstractKeyword)));

                found.Add(new TypeSeams(
                    name,
                    path,
                    behindInterface.Contains(name),
                    modifiers.Contains("static"),
                    modifiers.Contains("sealed"),
                    overridable,
                    AmbientsIn(declaration)));
            }
        }

        return new SeamSurvey(found
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList());
    }

    private static IReadOnlyList<Ambient> AmbientsIn(TypeDeclarationSyntax declaration)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        void Count(string name) =>
            counts[name] = counts.TryGetValue(name, out var seen) ? seen + 1 : 1;

        foreach (var access in declaration.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            // The owner is the part before the last dot, and it is written
            // either bare or fully qualified. `System.DateTime.Now` parses as
            // a member access whose expression is itself a member access, and
            // reading only the bare form would miss it in any file that
            // qualifies its types.
            var owner = access.Expression switch
            {
                IdentifierNameSyntax bare => bare.Identifier.Text,
                MemberAccessExpressionSyntax nested => nested.Name.Identifier.Text,
                _ => null,
            };

            if (owner is null) continue;

            var qualified = $"{owner}.{access.Name.Identifier.Text}";
            if (AmbientMembers.ContainsKey(qualified)) Count(qualified);
            else if (AmbientTypes.Contains(owner)) Count(owner);
        }

        foreach (var creation in declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var type = creation.Type switch
            {
                IdentifierNameSyntax simple => simple.Identifier.Text,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                _ => null,
            };

            if (type is not null && AmbientConstructions.Contains(type)) Count($"new {type}");
        }

        return counts
            .Select(pair => new Ambient(pair.Key, pair.Value))
            .OrderByDescending(a => a.Uses)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }
}
