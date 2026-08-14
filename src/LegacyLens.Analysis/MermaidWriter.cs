using System.Text;

namespace LegacyLens.Analysis;

/// <summary>
/// Renders a solution map as a Mermaid diagram.
///
/// Mermaid rather than Graphviz or an image: it renders inside GitHub, GitLab,
/// Azure DevOps wikis and most Markdown viewers with nothing to install. A
/// diagram nobody can open is a diagram nobody looks at.
/// </summary>
public class MermaidWriter
{
    /// <summary>
    /// Below this, a project is left out of the overview. Thirty-one boxes with
    /// ninety-four arrows is not a diagram, it is a wall. The count of what was
    /// dropped is always printed, so the reader knows the picture is partial.
    /// </summary>
    public int MinimumLines { get; init; } = 500;

    public bool IncludeTests { get; init; }

    public string Write(SolutionMap map)
    {
        var ids = new IdentifierTable();
        var shown = map.Projects
            .Where(p => IncludeTests || p.Kind != ProjectKind.Test)
            .Where(p => p.Lines >= MinimumLines)
            .ToList();

        var visible = shown.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = map.Projects.Count - shown.Count;

        var builder = new StringBuilder();
        builder.AppendLine("graph LR");

        // Grouped by the folder each project sits in. In a solution laid out by
        // hand this recovers the intended architecture for free: Libraries,
        // Presentation, Plugins. Where the layout is flat, the grouping is
        // simply absent rather than wrong.
        foreach (var group in shown.GroupBy(Layer).OrderBy(g => g.Key))
        {
            if (group.Key.Length > 0)
            {
                builder.AppendLine($"  subgraph {ids.For(group.Key, 'g')}[\"{Escape(group.Key)}\"]");
            }

            foreach (var project in group.OrderByDescending(p => p.Lines))
            {
                builder.AppendLine(
                    $"    {ids.For(project.Name)}[\"{Escape(project.Name)}<br/>"
                  + $"{project.Lines:N0} lines\"]");
            }

            if (group.Key.Length > 0) builder.AppendLine("  end");
        }

        builder.AppendLine();

        foreach (var edge in map.Edges.Where(e => visible.Contains(e.From) && visible.Contains(e.To)))
        {
            builder.AppendLine($"  {ids.For(edge.From)} --> {ids.For(edge.To)}");
        }

        builder.AppendLine();
        foreach (var kind in shown.Select(p => p.Kind).Distinct())
        {
            builder.AppendLine($"  classDef {kind.ToString().ToLowerInvariant()} {Style(kind)}");
        }

        foreach (var group in shown.GroupBy(p => p.Kind))
        {
            builder.AppendLine(
                $"  class {string.Join(',', group.Select(p => ids.For(p.Name)))} "
              + $"{group.Key.ToString().ToLowerInvariant()}");
        }

        if (hidden > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"  %% {hidden} project(s) omitted: under {MinimumLines:N0} lines"
                             + (IncludeTests ? "" : ", or test projects"));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The folder a project sits under, relative to the source root. Used as a
    /// grouping key, and empty when the solution is laid out flat.
    /// </summary>
    private static string Layer(ProjectInfo project)
    {
        var parts = project.Path.Replace('\\', '/').Split('/');
        // .../src/Libraries/Nop.Core/Nop.Core.csproj -> "Libraries"
        return parts.Length >= 3 ? parts[^3] : string.Empty;
    }

    /// <summary>
    /// Assigns each name a Mermaid-safe identifier, once, and remembers it.
    ///
    /// Sanitising on the fly is not enough. Dots and dashes both become
    /// underscores, so "A.B" and "A-B" collapse onto one id, and a shared id
    /// produces a diagram that parses but shows the wrong shape. That is worse
    /// than one that fails outright, because nobody notices.
    ///
    /// Nodes and subgraphs are prefixed differently for the same reason: in
    /// nopCommerce, Nop.Admin sits in a folder called Nop.Web, beside a project
    /// also called Nop.Web.
    /// </summary>
    private sealed class IdentifierTable
    {
        private readonly Dictionary<(string Name, char Prefix), string> _assigned = [];
        private readonly HashSet<string> _used = [];

        public string For(string name, char prefix = 'n')
        {
            if (_assigned.TryGetValue((name, prefix), out var existing)) return existing;

            var builder = new StringBuilder(prefix.ToString());
            foreach (var character in name)
            {
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');
            }

            var candidate = builder.ToString();
            var suffix = 2;
            while (!_used.Add(candidate))
            {
                candidate = $"{builder}_{suffix++}";
            }

            _assigned[(name, prefix)] = candidate;
            return candidate;
        }
    }

    private static string Escape(string text) => text.Replace("\"", "&quot;");

    private static string Style(ProjectKind kind) => kind switch
    {
        ProjectKind.Web      => "fill:#dbeafe,stroke:#2563eb,color:#1e3a5f",
        ProjectKind.Library  => "fill:#f1f5f9,stroke:#64748b,color:#1e293b",
        ProjectKind.Wpf      => "fill:#ede9fe,stroke:#7c3aed,color:#3b0764",
        ProjectKind.WinForms => "fill:#fef3c7,stroke:#d97706,color:#78350f",
        ProjectKind.Console  => "fill:#dcfce7,stroke:#16a34a,color:#14532d",
        ProjectKind.Test     => "fill:#fafafa,stroke:#a3a3a3,color:#525252",
        ProjectKind.Broken   => "fill:#fee2e2,stroke:#dc2626,color:#7f1d1d",
        _                    => "fill:#ffffff,stroke:#94a3b8,color:#334155",
    };
}
