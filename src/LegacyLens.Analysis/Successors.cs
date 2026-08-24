using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegacyLens.Analysis;

/// <summary>
/// One candidate replacement for a package, and what it maps.
///
/// A type mapping to null is not a gap in the catalogue. It is a recorded fact:
/// this thing exists in the old package and nothing in the new one does its
/// job. That is the answer a reader needs most, and it is the one a generated
/// catalogue would never contain, because a model asked for a successor always
/// finds one.
/// </summary>
public record Successor(
    string Package,
    string Note,
    IReadOnlyDictionary<string, string?> Types);

/// <summary>What replaces what, as data rather than as code.</summary>
public record SuccessorCatalogue(
    IReadOnlyDictionary<string, IReadOnlyList<Successor>> Packages,
    string Source)
{
    public IReadOnlyList<Successor> For(string package) =>
        Packages.TryGetValue(package, out var found) ? found : [];
}

/// <summary>How much of a codebase's usage one candidate covers.</summary>
public record Coverage(
    string Candidate,
    string Note,
    /// <summary>Types with a named replacement.</summary>
    IReadOnlyList<ApiUse> Covered,
    /// <summary>Types the catalogue says have no replacement. The blockers.</summary>
    IReadOnlyList<ApiUse> Unavailable,
    /// <summary>Types the catalogue says nothing about. Unknown, not fine.</summary>
    IReadOnlyList<ApiUse> Unknown)
{
    public int UsesCovered => Covered.Sum(t => t.Uses);
    public int UsesUnavailable => Unavailable.Sum(t => t.Uses);
    public int UsesUnknown => Unknown.Sum(t => t.Uses);
    public int Uses => UsesCovered + UsesUnavailable + UsesUnknown;

    /// <summary>
    /// Share of the calls this candidate answers for, as a percentage.
    ///
    /// Weighted by calls rather than by type, because a type used once and a
    /// type used five hundred times are not the same amount of work, and
    /// counting them equally is how a coverage figure becomes a lie.
    /// </summary>
    public int Percent => Uses == 0 ? 0 : (int)Math.Round(UsesCovered * 100.0 / Uses);

    /// <summary>Whether anything is known to have no replacement at all.</summary>
    public bool Blocked => Unavailable.Count > 0;
}

/// <summary>
/// Reads the catalogue and scores candidates against what a codebase uses.
///
/// The catalogue is a file, not a table in this assembly, for two reasons. It
/// grows with every migration anybody performs and should not need a rebuild to
/// do so. And it is the part that took the work: the engine around it is a
/// week, the knowledge in it is years.
/// </summary>
public class Successors
{
    private static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads a catalogue, or an empty one that says where it looked.
    ///
    /// Empty rather than thrown: the rest of the analysis works without a
    /// catalogue, and refusing to start because one file is missing would take
    /// the measurements down with it.
    /// </summary>
    public static SuccessorCatalogue Load(string? path = null)
    {
        var file = path ?? Default();

        if (file is null || !File.Exists(file))
        {
            return new SuccessorCatalogue(
                new Dictionary<string, IReadOnlyList<Successor>>(StringComparer.OrdinalIgnoreCase),
                file is null ? "no catalogue was found" : $"no catalogue at {file}");
        }

        try
        {
            // Read loosely first. This file is written and edited by hand, and
            // a hand-written file carries commentary: keys beginning with //
            // hold the reasoning, and a strict deserialise chokes on them
            // before anything else can be read.
            var read = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(file), Reading) ?? [];

            var packages = new Dictionary<string, IReadOnlyList<Successor>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in read)
            {
                if (name.StartsWith("//", StringComparison.Ordinal)) continue;
                if (value.ValueKind != JsonValueKind.Array) continue;

                var entries = value.Deserialize<List<Entry>>(Reading) ?? [];

                packages[name] = entries
                    .Select(candidate => new Successor(
                        candidate.Package,
                        candidate.Note ?? string.Empty,
                        candidate.Types ?? new Dictionary<string, string?>(StringComparer.Ordinal)))
                    .ToList();
            }

            return new SuccessorCatalogue(packages, file);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new SuccessorCatalogue(
                new Dictionary<string, IReadOnlyList<Successor>>(StringComparer.OrdinalIgnoreCase),
                $"{file} could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// Where the catalogue lives when nobody says.
    ///
    /// Beside the binary first, so a deployed copy carries its own, then up the
    /// tree, so a developer running from source finds the one in the repository.
    /// </summary>
    private static string? Default()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "successors.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "successors.json"),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var up = 0; up < 6 && directory is not null; up++)
        {
            candidates.Add(Path.Combine(directory.FullName, "data", "successors.json"));
            directory = directory.Parent;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Scores every candidate for a package against what the codebase uses.
    ///
    /// Three outcomes per type, never two. Replaced, known to have no
    /// replacement, or absent from the catalogue. Folding the last two together
    /// is what turns "we have not looked at this" into "this is fine".
    /// </summary>
    public IReadOnlyList<Coverage> Rank(UsageSurface surface, SuccessorCatalogue catalogue)
    {
        var candidates = catalogue.For(surface.Package);
        if (candidates.Count == 0) return [];

        return candidates
            .Select(candidate => Score(surface, candidate))
            .OrderByDescending(c => c.UsesCovered)
            .ThenBy(c => c.UsesUnavailable)
            .ToList();
    }

    private static Coverage Score(UsageSurface surface, Successor candidate)
    {
        var covered = new List<ApiUse>();
        var unavailable = new List<ApiUse>();
        var unknown = new List<ApiUse>();

        foreach (var use in surface.Types)
        {
            if (!candidate.Types.TryGetValue(use.Name, out var replacement)) unknown.Add(use);
            else if (replacement is null) unavailable.Add(use);
            else covered.Add(use);
        }

        return new Coverage(candidate.Package, candidate.Note, covered, unavailable, unknown);
    }

    /// <summary>What one type becomes, for a candidate that has an answer.</summary>
    public static string? Replacement(Successor candidate, string type) =>
        candidate.Types.TryGetValue(type, out var replacement) ? replacement : null;

    private sealed record Entry(
        [property: JsonPropertyName("package")] string Package,
        [property: JsonPropertyName("note")] string? Note,
        [property: JsonPropertyName("types")] Dictionary<string, string?>? Types);
}
