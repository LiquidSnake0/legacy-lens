using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LegacyLens.Analysis;

/// <summary>One project's conversion, as a patch and the reasons to read it.</summary>
public record ConversionProposal(
    string Project,
    string Patch,
    /// <summary>What the patch does not handle, stated rather than hidden.</summary>
    IReadOnlyList<string> Caveats)
{
    public bool IsEmpty => Patch.Length == 0;
}

/// <summary>
/// Converts `packages.config` to `PackageReference`, and produces a patch
/// rather than applying anything.
///
/// Every version written comes from the `packages.config` on disk. Nothing is
/// resolved, inferred or looked up, which is the whole point: the failure mode
/// reported against the tools this replaces is package references that do not
/// exist. A tool that only copies what it read cannot invent one.
///
/// The project file is edited as text, not reserialised through XDocument.
/// Round-tripping the XML would reformat the entire file and produce a patch
/// nobody can review, and an unreviewable patch is the same as no patch: the
/// deliverable here is a person's decision, not the write.
/// </summary>
public class PackagesConfigConversion
{
    /// <summary>
    /// A `Reference` whose hint path points inside the solution's `packages`
    /// folder was put there by packages.config, and PackageReference replaces
    /// it. One that points anywhere else was put there by a person and is left
    /// alone.
    /// </summary>
    private static readonly Regex HintPathIntoPackages = new(
        @"<HintPath>\s*\.{0,2}[\\/]*(?:\.\.[\\/])*packages[\\/]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Proposes the conversion for one project, or null when there is nothing
    /// to do or the project is not a candidate.
    /// </summary>
    /// <param name="project">A project the survey already classified.</param>
    /// <param name="rootPath">Repository root, so patch paths are relative to it.</param>
    public ConversionProposal? Propose(ProjectModernisation project, string rootPath)
    {
        if (project.Packages != PackageDeclaration.PackagesConfig) return null;

        var configPath = Path.Combine(Path.GetDirectoryName(project.Path)!, "packages.config");
        if (!File.Exists(configPath)) return null;

        List<(string Id, string Version)> packages;
        try
        {
            packages = ReadPackages(configPath);
        }
        catch (Exception)
        {
            return null;
        }

        if (packages.Count == 0) return null;

        var before = ReadVerbatim(project.Path);
        var caveats = new List<string>();

        // A package with no path to modern .NET still has to be declared, and
        // declaring it the modern way costs nothing. Refusing here was the same
        // category error the SDK conversion made, with a worse consequence:
        // that conversion drops the hint-path references and tells the reader
        // to convert packages first, and on nopCommerce 3.90 it could not be
        // done for twenty-six of the twenty-nine projects it had just offered.
        // The two have to compose or the output is a project file that will not
        // restore.
        if (project.Blocked)
        {
            caveats.Add(
                "Still depends on packages with no path to modern .NET: " +
                string.Join(", ", project.DeadEnds) +
                ". This changes how they are declared and not whether they have a future.");
        }

        var after = Rewrite(before, packages, caveats);

        var patch = new StringBuilder();
        patch.Append(UnifiedDiff.Between(Relative(project.Path, rootPath), before, after));
        patch.Append(UnifiedDiff.Deleting(Relative(configPath, rootPath), ReadVerbatim(configPath)));

        if (project.TargetFramework is not null &&
            (project.TargetFramework.StartsWith("v4.5", StringComparison.OrdinalIgnoreCase) ||
             project.TargetFramework.StartsWith("v4.0", StringComparison.OrdinalIgnoreCase) ||
             project.TargetFramework.StartsWith("v3", StringComparison.OrdinalIgnoreCase)))
        {
            caveats.Add(
                $"Targets {project.TargetFramework}. PackageReference is supported from 4.6.1 " +
                "onwards; on an older target the restore may behave differently.");
        }

        caveats.Add(
            "Binding redirects are not touched. They are generated from the old references and " +
            "dropping one is a runtime failure the build does not predict.");

        return new ConversionProposal(project.Name, patch.ToString(), caveats);
    }

    /// <summary>
    /// Every candidate in a survey, as one patch per project. Kept separate so
    /// a caller can review and apply them one at a time, which is the only way
    /// a conversion of eighty projects is reviewable at all.
    /// </summary>
    public IReadOnlyList<ConversionProposal> ProposeAll(ModernisationSurvey survey, string rootPath) =>
        survey.Projects
            .Select(p => Propose(p, rootPath))
            .OfType<ConversionProposal>()
            .Where(p => !p.IsEmpty)
            .OrderBy(p => p.Project, StringComparer.Ordinal)
            .ToList();

    private static List<(string Id, string Version)> ReadPackages(string configPath) =>
        XDocument.Load(configPath)
            .Descendants()
            .Where(e => e.Name.LocalName == "package")
            .Select(e => (
                Id: e.Attribute("id")?.Value ?? string.Empty,
                Version: e.Attribute("version")?.Value ?? string.Empty))
            .Where(p => p.Id.Length > 0 && p.Version.Length > 0)
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

    private static string Rewrite(
        string projectFile,
        List<(string Id, string Version)> packages,
        List<string> caveats)
    {
        // Split on the line feed only. Any carriage return stays attached to
        // its line, so a Windows-authored file is reproduced byte for byte and
        // the patch git compares against still matches.
        var lines = projectFile.Split('\n').ToList();
        var newline = projectFile.Contains("\r\n") ? "\r" : string.Empty;
        var kept = new List<string>();
        var removed = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (!line.TrimStart().StartsWith("<Reference ", StringComparison.OrdinalIgnoreCase) &&
                !line.TrimStart().StartsWith("<Reference>", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(line);
                continue;
            }

            // A Reference element runs to its closing tag unless it is self
            // closing. Capture it whole before deciding, because the HintPath
            // that identifies it sits on a later line.
            var block = new List<string> { line };
            var selfClosing = line.TrimEnd().EndsWith("/>", StringComparison.Ordinal);
            var j = i;

            while (!selfClosing && j + 1 < lines.Count)
            {
                j++;
                block.Add(lines[j]);
                if (lines[j].Contains("</Reference>", StringComparison.OrdinalIgnoreCase)) break;
            }

            var text = string.Join("\n", block);
            if (HintPathIntoPackages.IsMatch(text))
            {
                removed++;
                i = j;
            }
            else
            {
                kept.AddRange(block);
                i = j;
            }
        }

        if (removed == 0)
        {
            caveats.Add(
                "No assembly references pointing into the packages folder were found, so the old " +
                "references are left as they are. Check for duplicates after restoring.");
        }

        return InsertPackageReferences(kept, packages, DetectIndent(kept), newline);
    }

    /// <summary>
    /// Matches the file's own indentation rather than imposing one, so the
    /// patch shows a new item group and not a whitespace argument.
    /// </summary>
    private static string DetectIndent(List<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("<ItemGroup", StringComparison.OrdinalIgnoreCase))
            {
                var indent = line[..(line.Length - line.TrimStart().Length)];
                if (indent.Length > 0) return indent;
            }
        }

        return "  ";
    }

    private static string InsertPackageReferences(
        List<string> lines,
        List<(string Id, string Version)> packages,
        string indent,
        string newline)
    {
        var group = new List<string> { $"{indent}<ItemGroup>{newline}" };
        group.AddRange(packages.Select(p =>
            $"{indent}{indent}<PackageReference Include=\"{p.Id}\" Version=\"{p.Version}\" />{newline}"));
        group.Add($"{indent}</ItemGroup>{newline}");

        var closing = lines.FindLastIndex(l =>
            l.Contains("</Project>", StringComparison.OrdinalIgnoreCase));

        if (closing < 0)
        {
            // No closing tag means the file is not what it claimed to be.
            // Appending would produce invalid XML, so nothing is changed.
            return string.Join("\n", lines);
        }

        lines.InsertRange(closing, group);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Reads a file without letting the byte order mark be swallowed.
    ///
    /// `File.ReadAllText` detects a BOM and strips it, which is helpful
    /// everywhere except here: the patch has to reproduce the bytes on disk,
    /// and a first line missing three bytes is a first line that does not
    /// match. Visual Studio writes project files with a BOM, so this is the
    /// common case rather than the exception.
    /// </summary>
    private static string ReadVerbatim(string path)
    {
        using var reader = new StreamReader(
            path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static string Relative(string path, string rootPath) =>
        Path.GetRelativePath(rootPath, path).Replace('\\', '/');
}
