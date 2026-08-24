using System.Text;
using System.Xml.Linq;

namespace LegacyLens.Analysis;

/// <summary>
/// What stands between a project file and the SDK format, or nothing.
///
/// The verdict is the deliverable. A patch is produced only when there is
/// nothing in the way, and the far more common answer is a named reason.
/// </summary>
public record SdkConversionVerdict(
    string Project,
    IReadOnlyList<string> Blockers,
    ConversionProposal? Proposal)
{
    public bool Convertible => Blockers.Count == 0;
}

/// <summary>
/// Rewrites a pre-SDK project file in the SDK format, and refuses whenever the
/// old file carries something the new one has no place for.
///
/// The refusals matter more than the conversions. A pre-SDK file is a hundred
/// and fifty lines of which the SDK supplies all but ten, so the transformation
/// itself is close to trivial; what is not trivial is knowing when the ninety
/// lines being deleted contained the one that mattered. Custom targets, a
/// `ProjectExtensions` block and a non-standard import are each load-bearing
/// and each have no equivalent, so a project carrying any of them is reported
/// rather than converted.
/// </summary>
public class SdkStyleConversion
{
    /// <summary>The two imports every pre-SDK C# project has and the SDK replaces.</summary>
    private static readonly string[] StandardImports =
        ["Microsoft.Common.props", "Microsoft.CSharp.targets"];

    /// <summary>
    /// Properties the SDK either supplies or no longer reads. Dropping one of
    /// these loses nothing; dropping anything else would, which is why the
    /// unknown ones are carried over instead.
    /// </summary>
    private static readonly HashSet<string> SuppliedBySdk = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration", "Platform", "ProductVersion", "SchemaVersion", "ProjectGuid",
        "AppDesignerFolder", "FileUpgradeFlags", "OldToolsVersion", "UpgradeBackupLocation",
        "TargetFrameworkProfile", "TargetFrameworkVersion", "DebugSymbols", "DebugType",
        "Optimize", "OutputPath", "DefineConstants", "ErrorReport", "WarningLevel",
        "Prefer32Bit", "VisualStudioVersion", "VSToolsPath", "ProjectTypeGuids",
        "RestorePackages", "SolutionDir", "NuGetPackageImportStamp", "AutoGenerateBindingRedirects",
    };

    /// <summary>Item types the SDK includes on its own from the folder.</summary>
    private static readonly HashSet<string> GlobbedBySdk = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "EmbeddedResource", "None", "Content",
    };

    public SdkConversionVerdict Propose(ProjectModernisation project, string rootPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(project.Path);
        }
        catch (Exception exception)
        {
            return new SdkConversionVerdict(
                project.Name, [$"The project file could not be parsed: {exception.GetType().Name}."], null);
        }

        var root = document.Root;
        if (root is null)
            return new SdkConversionVerdict(project.Name, ["The project file is empty."], null);

        if (root.Attribute("Sdk") is not null)
            return new SdkConversionVerdict(project.Name, ["Already in the SDK format."], null);

        var ns = root.Name.Namespace;
        var blockers = Blockers(root, ns, project).ToList();
        if (blockers.Count > 0)
            return new SdkConversionVerdict(project.Name, blockers, null);

        var before = ReadVerbatim(project.Path);
        var caveats = new List<string>();
        var after = Rewrite(root, ns, project, before, caveats);

        var patch = UnifiedDiff.Between(
            Path.GetRelativePath(rootPath, project.Path).Replace('\\', '/'), before, after);

        return new SdkConversionVerdict(
            project.Name, [], new ConversionProposal(project.Name, patch, caveats));
    }

    public IReadOnlyList<SdkConversionVerdict> Judge(ModernisationSurvey survey, string rootPath) =>
        survey.Projects
            .Where(p => !p.SdkStyle)
            .Select(p => Propose(p, rootPath))
            .OrderBy(v => v.Project, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> Blockers(XElement root, XNamespace ns, ProjectModernisation project)
    {
        if (project.Blocked)
        {
            yield return
                "Depends on packages with no path to modern .NET: " +
                string.Join(", ", project.DeadEnds) +
                ". Converting the file format would not make it port.";
        }

        var targets = root.Elements(ns + "Target").Count();
        if (targets > 0)
        {
            yield return
                $"Carries {targets} custom build target(s). The SDK has no equivalent and the " +
                "build steps would be silently lost.";
        }

        if (root.Elements(ns + "ProjectExtensions").Any())
        {
            yield return
                "Carries a ProjectExtensions block, which holds the project flavour. Web and " +
                "test flavours change how the project is built and are not expressible here.";
        }

        var imports = root.Elements(ns + "Import")
            .Select(i => i.Attribute("Project")?.Value ?? string.Empty)
            .Where(path => !StandardImports.Any(std => path.Contains(std, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (imports.Count > 0)
        {
            yield return
                $"Imports {imports.Count} file(s) the SDK does not supply: " +
                string.Join(", ", imports.Select(Path.GetFileName).Distinct()) + ".";
        }

        if (project.TargetFramework is null)
            yield return "No TargetFrameworkVersion, so there is nothing to translate it to.";
    }

    private static string Rewrite(
        XElement root,
        XNamespace ns,
        ProjectModernisation project,
        string before,
        List<string> caveats)
    {
        var newline = before.Contains("\r\n") ? "\r\n" : "\n";
        var lines = new List<string> { "<Project Sdk=\"Microsoft.NET.Sdk\">", "" };

        var properties = new List<string>
        {
            $"    <TargetFramework>{Moniker(project.TargetFramework!)}</TargetFramework>",
        };

        var outputType = Property(root, ns, "OutputType");
        if (outputType is not null && !outputType.Equals("Library", StringComparison.OrdinalIgnoreCase))
            properties.Add($"    <OutputType>{outputType}</OutputType>");

        // The SDK defaults both to the file name. Written only when the old
        // file disagreed with it, because a redundant property is one more
        // thing for a reader to check.
        foreach (var name in new[] { "RootNamespace", "AssemblyName" })
        {
            var value = Property(root, ns, name);
            if (value is not null && !value.Equals(project.Name, StringComparison.Ordinal))
                properties.Add($"    <{name}>{value}</{name}>");
        }

        var carried = root.Elements(ns + "PropertyGroup")
            .SelectMany(g => g.Elements())
            .Where(e => !SuppliedBySdk.Contains(e.Name.LocalName))
            .Where(e => e.Name.LocalName is not ("OutputType" or "RootNamespace" or "AssemblyName"))
            .Where(e => !string.IsNullOrWhiteSpace(e.Value))
            .GroupBy(e => e.Name.LocalName, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        foreach (var property in carried)
            properties.Add($"    <{property.Name.LocalName}>{property.Value.Trim()}</{property.Name.LocalName}>");

        lines.Add("  <PropertyGroup>");
        lines.AddRange(properties);
        lines.Add("  </PropertyGroup>");

        AddItems(lines, root, ns, "PackageReference", caveats);
        AddItems(lines, root, ns, "ProjectReference", caveats);
        AddBareReferences(lines, root, ns, caveats);

        var globbed = root.Elements(ns + "ItemGroup")
            .SelectMany(g => g.Elements())
            .Count(e => GlobbedBySdk.Contains(e.Name.LocalName));

        if (globbed > 0)
        {
            caveats.Add(
                $"{globbed} Compile, Content, None and EmbeddedResource item(s) are dropped: the SDK " +
                "includes them from the folder. Anything deliberately excluded from the build was " +
                "excluded by not being listed, and that exclusion is now gone.");
        }

        caveats.Add(
            "Build configurations are dropped. Debug and Release come from the SDK, and any " +
            "non-default OutputPath or DefineConstants went with them.");

        if (carried.Count > 0)
        {
            caveats.Add(
                $"{carried.Count} propert(y/ies) not recognised were carried over verbatim rather " +
                "than dropped, so some may be settings the SDK ignores. Deleting a property this " +
                "tool did not understand is the one mistake it cannot detect, so it keeps them.");
        }

        lines.Add("</Project>");
        return string.Join(newline, lines) + newline;
    }

    private static void AddItems(
        List<string> lines, XElement root, XNamespace ns, string name, List<string> caveats)
    {
        var items = root.Elements(ns + "ItemGroup")
            .SelectMany(g => g.Elements(ns + name))
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        if (items.Count == 0) return;

        lines.Add("");
        lines.Add("  <ItemGroup>");
        foreach (var include in items)
        {
            if (name == "PackageReference")
            {
                var version = root.Elements(ns + "ItemGroup")
                    .SelectMany(g => g.Elements(ns + name))
                    .FirstOrDefault(e => e.Attribute("Include")?.Value == include)
                    ?.Attribute("Version")?.Value;

                lines.Add($"    <PackageReference Include=\"{include}\" Version=\"{version}\" />");
            }
            else
            {
                lines.Add($"    <{name} Include=\"{include.Replace('\\', '/')}\" />");
            }
        }

        lines.Add("  </ItemGroup>");
    }

    /// <summary>
    /// References with no hint path. On .NET Framework these resolve from the
    /// GAC, and the SDK still honours them for a Framework target, so they are
    /// carried rather than dropped. A reference into the packages folder is not
    /// carried: PackageReference replaces it, and keeping both is a duplicate.
    /// </summary>
    private static void AddBareReferences(
        List<string> lines, XElement root, XNamespace ns, List<string> caveats)
    {
        var references = root.Elements(ns + "ItemGroup")
            .SelectMany(g => g.Elements(ns + "Reference"))
            .Where(e => e.Element(ns + "HintPath") is null)
            .Select(e => e.Attribute("Include")?.Value?.Split(',')[0].Trim())
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        var withHint = root.Elements(ns + "ItemGroup")
            .SelectMany(g => g.Elements(ns + "Reference"))
            .Count(e => e.Element(ns + "HintPath") is not null);

        if (withHint > 0)
        {
            caveats.Add(
                $"{withHint} reference(s) with a hint path are dropped. They came from " +
                "packages.config and PackageReference replaces them; convert that first if it " +
                "has not been done.");
        }

        if (references.Count == 0) return;

        lines.Add("");
        lines.Add("  <ItemGroup>");
        foreach (var id in references) lines.Add($"    <Reference Include=\"{id}\" />");
        lines.Add("  </ItemGroup>");
    }

    private static string? Property(XElement root, XNamespace ns, string name) =>
        root.Elements(ns + "PropertyGroup")
            .Elements(ns + name)
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);

    /// <summary>
    /// `v4.8` becomes `net48`. The old form carries dots and a leading v, the
    /// new one carries neither.
    /// </summary>
    public static string Moniker(string targetFrameworkVersion) =>
        "net" + targetFrameworkVersion.TrimStart('v', 'V').Replace(".", string.Empty);

    private static string ReadVerbatim(string path)
    {
        using var reader = new StreamReader(
            path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
}
