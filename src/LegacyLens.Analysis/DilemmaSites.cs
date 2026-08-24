using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LegacyLens.Analysis;

/// <summary>One place in the code that raises a dilemma.</summary>
public record Site(string Path, int Line, string Name, string Text);

/// <summary>A dilemma, and the lines that raised it.</summary>
public record Raised(Dilemma Dilemma, IReadOnlyList<Site> Sites)
{
    public int Files => Sites.Select(s => s.Path).Distinct(StringComparer.Ordinal).Count();
}

/// <summary>
/// Where in the code a decision has to be made.
///
/// Every question this tool asks names a line, and this is where those lines
/// come from. A question with no reference to the code is a generic
/// questionnaire, and it reads as one by the second screen: the reader
/// recognises immediately that nothing was read before they were asked.
/// </summary>
public class DilemmaSites
{
    private static readonly string[] SkipDirectories =
        [".git", "bin", "obj", "node_modules", "packages", ".vs"];

    /// <summary>How many sites are kept per dilemma. Enough to show, not to scroll.</summary>
    private const int Shown = 12;

    public IReadOnlyList<Raised> Find(string rootPath, DilemmaCatalogue catalogue)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        // One pass over the tree for every dilemma at once: reading a large
        // estate three times to answer three questions is three times the wait
        // for the same answer.
        var wanted = catalogue.Dilemmas
            .SelectMany(d => Spellings(d.Triggers).Select(w => (w.Name, Want: new Want(d, w.Indexed))))
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Want).ToList(), StringComparer.Ordinal);

        if (wanted.Count == 0) return [];

        var found = new Dictionary<string, List<Site>>(StringComparer.Ordinal);

        foreach (var path in Walk(rootPath))
        {
            foreach (var (site, indexed) in SitesIn(path, wanted.Keys))
            {
                foreach (var want in wanted[site.Name])
                {
                    // A trigger written `Session[]` only counts where the name
                    // is indexed. See Spellings for why that earns its place.
                    if (want.Indexed && !indexed) continue;

                    if (!found.TryGetValue(want.Dilemma.Id, out var sites))
                        found[want.Dilemma.Id] = sites = [];

                    sites.Add(site);
                }
            }
        }

        return catalogue.Dilemmas
            .Where(d => found.ContainsKey(d.Id))
            .Select(d => new Raised(d, found[d.Id]
                // One line is one place to look, however many of the names on
                // it are triggers. `[SessionState(SessionStateBehavior.X)]`
                // matched twice and printed the same line twice, which reads
                // as a bug in the tool rather than as two findings.
                .GroupBy(s => (s.Path, s.Line))
                .Select(g => g.First())
                .OrderBy(s => s.Path, StringComparer.Ordinal)
                .ThenBy(s => s.Line)
                .Take(Shown)
                .ToList()))
            .OrderByDescending(r => r.Sites.Count)
            .ToList();
    }

    /// <summary>One dilemma's claim on a name, and the shape it requires.</summary>
    private record Want(Dilemma Dilemma, bool Indexed);

    /// <summary>
    /// What a trigger matches, and in what shape.
    ///
    /// Two rules, both of them about the language rather than about any one
    /// dilemma, which is why they live here instead of being spelled out
    /// entry by entry in a file somebody has to remember to spell them in.
    ///
    /// **An attribute can be written short.** C# lets `[SessionState]` mean
    /// `SessionStateAttribute`, and the short form is the one people write. A
    /// catalogue naming the type read a textbook session controller and raised
    /// nothing at all.
    ///
    /// **A trigger ending in `[]` only counts where the name is indexed.**
    /// Measured on Orchard: 62 mentions of `Session`, of which 6 are ASP.NET
    /// session state and 56 are NHibernate's. Every one of the six is
    /// `Session[...]`, and NHibernate's `ISession` has no indexer, so the
    /// shape separates them where the name alone cannot. Without it the
    /// dilemma is raised mostly by an ORM that has nothing to do with it, and
    /// a panel that is ninety per cent wrong is worse than one that is empty.
    /// </summary>
    private static IEnumerable<(string Name, bool Indexed)> Spellings(IEnumerable<string> triggers)
    {
        foreach (var trigger in triggers)
        {
            if (trigger.EndsWith("[]", StringComparison.Ordinal) && trigger.Length > 2)
            {
                yield return (trigger[..^2], true);
                continue;
            }

            yield return (trigger, false);

            if (trigger.EndsWith("Attribute", StringComparison.Ordinal)
                && trigger.Length > "Attribute".Length)
            {
                yield return (trigger[..^"Attribute".Length], false);
            }
        }
    }

    /// <summary>
    /// Every mention of a wanted name, with its line and the line's text.
    ///
    /// Matched on the name wherever it appears rather than only in a type
    /// position, unlike the usage surface. The purpose is different: there the
    /// question was how much of a package is used, and a member access would
    /// have inflated it. Here the question is where to point a reader, and
    /// `HttpContext.Current` on line 47 is exactly where they should look.
    /// </summary>
    private static IEnumerable<(Site Site, bool Indexed)> SitesIn(
        string path, IEnumerable<string> names)
    {
        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        // Cheap rejection before parsing. Most files mention none of these, and
        // parsing every file in an estate to find out is the slow way round.
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        if (!wanted.Any(name => source.Contains(name, StringComparison.Ordinal))) yield break;

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(source).GetRoot();
        }
        catch (Exception)
        {
            yield break;
        }

        var lines = source.Replace("\r\n", "\n").Split('\n');

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var text = name.Identifier.Text;
            if (!wanted.Contains(text)) continue;

            // A using is not a decision. `using System.Web.SessionState;`
            // matched on the last segment of the namespace and put the top of
            // every file in the list, which is noise and a line that says
            // nothing about what the code does with it. Found by running it.
            if (Declaring(name)) continue;

            var line = name.GetLocation().GetLineSpan().StartLinePosition.Line;

            yield return (
                new Site(
                    path,
                    line + 1,
                    text,
                    line < lines.Length ? lines[line].Trim() : string.Empty),
                Indexed(name));
        }
    }

    /// <summary>
    /// Whether this occurrence of the name is being indexed.
    ///
    /// `Session["cart"]` rather than `session.Query`. The name is the whole of
    /// the thing being indexed, so a member access that ends in it counts and
    /// one that merely contains it does not.
    /// </summary>
    private static bool Indexed(SyntaxNode name)
    {
        var outermost = name;

        while (outermost.Parent is MemberAccessExpressionSyntax member
               && member.Name == outermost)
        {
            outermost = member;
        }

        return outermost.Parent is ElementAccessExpressionSyntax access
               && access.Expression == outermost;
    }

    /// <summary>
    /// Whether a name is only naming a namespace rather than using anything.
    ///
    /// A using and the header of a namespace both mention names without doing
    /// anything with them, and neither is somewhere a reader could go to see
    /// the problem.
    /// </summary>
    private static bool Declaring(SyntaxNode name) =>
        name.Ancestors().Any(a => a is UsingDirectiveSyntax)
        || name.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Any(ns => ns.Name.Span.Contains(name.Span));

    private static IEnumerable<string> Walk(string directory)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                if (SkipDirectories.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                    continue;

                foreach (var found in Walk(entry)) yield return found;
            }
            else if (entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }
}
