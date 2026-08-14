namespace LegacyLens.Analysis;

/// <summary>What a project is, as far as its file and its folder can tell.</summary>
public record ProjectInfo(
    string Name,
    string Path,
    ProjectKind Kind,
    string? TargetFramework,
    /// <summary>Other projects in the solution it depends on.</summary>
    IReadOnlyList<string> References,
    /// <summary>Assemblies and packages it references, used to spot coupling.</summary>
    IReadOnlyList<string> AssemblyReferences,
    int SourceFiles,
    int Lines)
{
    /// <summary>Targets .NET Framework rather than .NET Core or later.</summary>
    public bool IsLegacyFramework =>
        TargetFramework is not null &&
        (TargetFramework.StartsWith("v3") || TargetFramework.StartsWith("v4") ||
         TargetFramework.StartsWith("net3") || TargetFramework.StartsWith("net4"));
}

/// <summary>
/// Determined by what sits in the project folder, not by which assemblies the
/// project references.
///
/// Referenced assemblies lie: nopCommerce's Nop.Core references System.Web.Mvc
/// and is still a class library. A web.config next to a Views folder does not
/// lie. What the references reveal is reported separately, as a finding.
/// </summary>
public enum ProjectKind
{
    Unknown,
    Library,
    Console,
    /// <summary>Has a web.config, a Global.asax, or Views and Controllers folders.</summary>
    Web,
    /// <summary>Has an App.xaml, or declares UseWPF.</summary>
    Wpf,
    /// <summary>Has .Designer.cs form files, or declares UseWindowsForms.</summary>
    WinForms,
    /// <summary>References a test framework, whatever its output type says.</summary>
    Test,
    /// <summary>The project file could not be parsed. Reported, not hidden.</summary>
    Broken,
}

/// <summary>A dependency, from the project that declares it to the one it needs.</summary>
public record ProjectEdge(string From, string To);

public record SolutionMap(
    IReadOnlyList<ProjectInfo> Projects,
    IReadOnlyList<ProjectEdge> Edges,
    IReadOnlyList<IReadOnlyList<string>> Cycles)
{
    public int TotalLines => Projects.Sum(p => p.Lines);
    public int TotalFiles => Projects.Sum(p => p.SourceFiles);
}
