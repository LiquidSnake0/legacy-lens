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
            .SelectMany(d => Spellings(d.Triggers).Select(t => (Trigger: t, Dilemma: d)))
            .GroupBy(x => x.Trigger, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Distinct().Select(x => x.Dilemma).ToList(), StringComparer.Ordinal);

        if (wanted.Count == 0) return [];

        var found = new Dictionary<string, List<Site>>(StringComparer.Ordinal);

        foreach (var path in Walk(rootPath))
        {
            foreach (var site in SitesIn(path, wanted.Keys))
            {
                foreach (var dilemma in wanted[site.Name])
                {
                    if (!found.TryGetValue(dilemma.Id, out var sites))
                        found[dilemma.Id] = sites = [];

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

    /// <summary>
    /// Both ways an attribute can be written.
    ///
    /// C# lets `[SessionState]` mean `SessionStateAttribute`, and the short
    /// form is the one people actually write. Found by running this against a
    /// textbook session-state controller and watching it raise nothing: the
    /// catalogue named the type and the code named the attribute.
    ///
    /// Handled here rather than by writing both spellings into the catalogue,
    /// because it is a rule of the language and not a fact about any one
    /// dilemma. Written in the catalogue it would have to be remembered every
    /// time, and it would be forgotten.
    /// </summary>
    private static IEnumerable<string> Spellings(IEnumerable<string> triggers)
    {
        foreach (var trigger in triggers)
        {
            yield return trigger;

            if (trigger.EndsWith("Attribute", StringComparison.Ordinal)
                && trigger.Length > "Attribute".Length)
            {
                yield return trigger[..^"Attribute".Length];
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
    private static IEnumerable<Site> SitesIn(string path, IEnumerable<string> names)
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

            yield return new Site(
                path,
                line + 1,
                text,
                line < lines.Length ? lines[line].Trim() : string.Empty);
        }
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
