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
    IReadOnlyDictionary<string, string?> Types)
{
    /// <summary>
    /// The correspondences that were read off finished migrations rather than
    /// written from knowledge, and where each one was seen.
    ///
    /// Both kinds are used the same way and only one of them can be checked
    /// against anything. Keeping them apart is what lets the tool say which is
    /// which, and it is the difference between "somebody believes this" and
    /// "four teams did this".
    ///
    /// A type here is also in <see cref="Types"/>, mapped to its own name,
    /// because that is what was observed: the type kept its name and changed
    /// namespace.
    /// </summary>
    public IReadOnlyDictionary<string, string> Observed { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

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

                packages[name] = entries.Select(Read).ToList();
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
    /// Where the catalogue lives when nobody says. See <see cref="Catalogues"/>.
    /// </summary>
    private static string? Default() => Catalogues.Find("successors.json");

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

    /// <summary>
    /// One entry, with what was written and what was measured merged into the
    /// same map.
    ///
    /// An observed correspondence maps a name to itself, because that is what
    /// was seen: the type kept its name and changed namespace. Written entries
    /// win where the two overlap, since somebody looked at that one on purpose.
    /// </summary>
    private static Successor Read(Entry candidate)
    {
        var types = new Dictionary<string, string?>(
            candidate.Types ?? new Dictionary<string, string?>(), StringComparer.Ordinal);

        var observed = candidate.Observed ?? new Dictionary<string, string>();

        foreach (var name in observed.Keys)
            if (!types.ContainsKey(name)) types[name] = name;

        return new Successor(candidate.Package, candidate.Note ?? string.Empty, types)
        {
            Observed = new Dictionary<string, string>(observed, StringComparer.Ordinal),
        };
    }

    private sealed record Entry(
        [property: JsonPropertyName("package")] string Package,
        [property: JsonPropertyName("note")] string? Note,
        [property: JsonPropertyName("types")] Dictionary<string, string?>? Types,
        /// <summary>Name to where it was seen. Read off migrations, not written.</summary>
        [property: JsonPropertyName("observed")] Dictionary<string, string>? Observed);
}
