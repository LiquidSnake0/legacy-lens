using System.Text;
using System.Text.RegularExpressions;

namespace LegacyLens.Analysis;

/// <summary>
/// A NuGet version, comparable.
///
/// Parsed rather than compared as text, because "10.0.0" sorts before "9.0.0"
/// as a string and picking the wrong winner here means downgrading an estate.
/// </summary>
public readonly record struct PackageVersion(
    int Major, int Minor, int Patch, int Revision, string? Prerelease, string Original)
    : IComparable<PackageVersion>
{
    private static readonly Regex Shape = new(
        @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled);

    public static bool TryParse(string text, out PackageVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var match = Shape.Match(text.Trim());
        if (!match.Success) return false;

        int Part(int group) =>
            match.Groups[group].Success ? int.Parse(match.Groups[group].Value) : 0;

        version = new PackageVersion(
            Part(1), Part(2), Part(3), Part(4),
            match.Groups[5].Success ? match.Groups[5].Value : null,
            text.Trim());

        return true;
    }

    public int CompareTo(PackageVersion other)
    {
        var numbers = (Major, Minor, Patch, Revision).CompareTo(
            (other.Major, other.Minor, other.Patch, other.Revision));

        if (numbers != 0) return numbers;

        // A prerelease sorts below the release it leads to: 2.0.0-beta is
        // older than 2.0.0, which is the opposite of how the strings sort.
        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (mine, theirs) => string.CompareOrdinal(mine, theirs),
        };
    }
}

/// <summary>What should happen to one package's versions, and why.</summary>
public record UnificationVerdict(
    string PackageId,
    IReadOnlyList<string> Found,
    string? Chosen,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings)
{
    public bool Divergent => Found.Count > 1;
    public bool Unifiable => Divergent && Chosen is not null && Blockers.Count == 0;
}

/// <summary>
/// Brings a package to one version across the estate.
///
/// This is the conversion nobody puts in a demo, and the one that decides
/// whether the others are safe. The same package pinned to three versions in
/// three projects is what binding redirects exist to paper over, and a format
/// conversion performed on top of that disagreement carries it forward
/// silently.
///
/// Every version written is one already present on disk. Nothing is looked up.
/// Choosing the newest of what is there cannot invent a package that does not
/// exist, which is the failure mode reported against the tools this replaces;
/// asking nuget.org for something newer could, and would also mean the answer
/// changes between two runs over the same unchanged repository.
/// </summary>
public class PackageUnification
{
    /// <summary>
    /// Where a version lives in a `packages.config` entry, captured so the
    /// rewrite replaces the version and touches nothing else on the line.
    /// </summary>
    private static Regex ConfigEntry(string id) => new(
        $@"(<package\s+id\s*=\s*""{Regex.Escape(id)}""[^>]*?\sversion\s*=\s*"")([^""]*)("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex ReferenceAttribute(string id) => new(
        $@"(<PackageReference\s+Include\s*=\s*""{Regex.Escape(id)}""[^>]*?\sVersion\s*=\s*"")([^""]*)("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The nested form: a Version element inside PackageReference.</summary>
    private static Regex ReferenceElement(string id) => new(
        $@"(<PackageReference\s+Include\s*=\s*""{Regex.Escape(id)}""[^>]*>\s*<Version>)([^<]*)(</Version>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Judges every package the survey found, whether or not it diverges.
    ///
    /// The ones that agree are reported too. "Nothing to do here, and it was
    /// checked" is a different answer from silence, and on a tended estate it
    /// is the whole result.
    /// </summary>
    public IReadOnlyList<UnificationVerdict> Judge(ModernisationSurvey survey) =>
        survey.Packages.Select(Judge).ToList();

    private static UnificationVerdict Judge(PackageUse package)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        var parsed = new List<PackageVersion>();
        foreach (var text in package.Versions)
        {
            if (PackageVersion.TryParse(text, out var version)) parsed.Add(version);
            else blockers.Add($"Version \"{text}\" is not a version this can order.");
        }

        if (parsed.Count == 0 || blockers.Count > 0)
            return new UnificationVerdict(package.Id, package.Versions, null, blockers, warnings);

        var chosen = parsed.Max();

        // The one that has to be said out loud. Nothing in a version number
        // says whether the API changed, and unifying across a major is a code
        // change wearing the clothes of a configuration change.
        if (package.Versions.Count > 1 && parsed.Min().Major != chosen.Major)
        {
            warnings.Add(
                $"Crosses a major version, {parsed.Min().Major} to {chosen.Major}. " +
                "Nothing in a version number says whether the API changed, so this " +
                "one is a code change until somebody has read the release notes.");
        }

        if (package.Portability == Portability.TiedToSystemWeb)
        {
            warnings.Add(
                "Tied to System.Web, so no version of it ports. Worth unifying " +
                "anyway: the build gets one answer instead of three, and that is " +
                "true whether or not the package has a future.");
        }

        return new UnificationVerdict(
            package.Id, package.Versions, chosen.Original, blockers, warnings);
    }

    /// <summary>
    /// A patch bringing every divergent package to one version, or null when
    /// there is nothing to unify.
    /// </summary>
    public ConversionProposal? Propose(ModernisationSurvey survey, string rootPath)
    {
        var unifiable = Judge(survey).Where(v => v.Unifiable).ToList();
        if (unifiable.Count == 0) return null;

        var patch = new StringBuilder();
        var caveats = new List<string>();
        var touched = 0;

        // Grouped by file rather than by package: two packages changing in one
        // project file have to arrive as one hunk set, or the second patch
        // applies against text the first one already moved.
        foreach (var file in FilesHolding(survey))
        {
            string before;
            try
            {
                before = ReadVerbatim(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var after = before;
            foreach (var verdict in unifiable) after = Rewrite(after, verdict.PackageId, verdict.Chosen!);

            if (after == before) continue;

            patch.Append(UnifiedDiff.Between(Relative(file, rootPath), before, after));
            touched++;
        }

        if (touched == 0) return null;

        foreach (var verdict in unifiable)
        {
            caveats.Add(
                $"{verdict.PackageId}: {string.Join(", ", verdict.Found)} becomes {verdict.Chosen}.");

            foreach (var warning in verdict.Warnings) caveats.Add($"  {warning}");
        }

        var redirects = RedirectsFor(unifiable.Select(v => v.PackageId).ToList(), rootPath);
        if (redirects.Count > 0)
        {
            caveats.Add(
                $"{redirects.Count} binding redirect(s) name these packages and are left " +
                "untouched: " + string.Join(", ", redirects.Take(6)) +
                (redirects.Count > 6 ? ", and more" : "") + ".");

            caveats.Add(
                "  Not edited, and not an oversight. A redirect names an assembly " +
                "version, which is not the package version and cannot be derived " +
                "from it by reading these files. Guessing one produces a build that " +
                "succeeds and an application that throws on first use.");
        }

        return new ConversionProposal("packages", patch.ToString(), caveats);
    }

    /// <summary>
    /// Every file that could name a package version: the project files and the
    /// `packages.config` beside them.
    /// </summary>
    private static IEnumerable<string> FilesHolding(ModernisationSurvey survey)
    {
        foreach (var project in survey.Projects)
        {
            yield return project.Path;

            var config = Path.Combine(Path.GetDirectoryName(project.Path)!, "packages.config");
            if (File.Exists(config)) yield return config;
        }
    }

    private static string Rewrite(string text, string id, string version)
    {
        text = ConfigEntry(id).Replace(text, m => m.Groups[1].Value + version + m.Groups[3].Value);
        text = ReferenceAttribute(id).Replace(text, m => m.Groups[1].Value + version + m.Groups[3].Value);
        text = ReferenceElement(id).Replace(text, m => m.Groups[1].Value + version + m.Groups[3].Value);
        return text;
    }

    /// <summary>
    /// Config files carrying a redirect for one of these packages.
    ///
    /// Matched on the assembly name equalling the package id, which is the
    /// common case and not a rule. It over-reports rather than under-reports:
    /// a redirect named here that turns out to be unrelated costs a reader ten
    /// seconds, and one missed costs them a production incident.
    /// </summary>
    private static IReadOnlyList<string> RedirectsFor(IReadOnlyList<string> ids, string rootPath)
    {
        var found = new List<string>();

        foreach (var file in ConfigFiles(rootPath))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!text.Contains("<bindingRedirect", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var id in ids)
            {
                if (text.Contains($"name=\"{id}\"", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add($"{Relative(file, rootPath)} ({id})");
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> ConfigFiles(string rootPath)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(rootPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (name is ".git" or "bin" or "obj" or "node_modules" or "packages" or ".vs") continue;

                foreach (var found in ConfigFiles(entry)) yield return found;
            }
            else
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("web.config", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("app.config", StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>
    /// Reads without letting the reader decide anything about encoding.
    ///
    /// A byte order mark has to survive: File.ReadAllText strips one, and the
    /// patch is then three bytes short of the file on disk and git refuses it.
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
