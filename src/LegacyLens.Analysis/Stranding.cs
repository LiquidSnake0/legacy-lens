using System.Text.Json;

namespace LegacyLens.Analysis;

/// <summary>Whether a package has any life left on the target, and why somebody says so.</summary>
public record Stranding(bool Strands, string Why);

/// <summary>
/// Which packages cannot stay, whatever anyone would prefer.
///
/// A separate judgement from what replaces what, and a more decisive one.
/// `System.Web.Mvc` is built on `System.Web`, which ASP.NET Core does not have
/// and never will, so a team moving there ports it whether a successor covers
/// eight per cent of their calls or sixty-eight. `Newtonsoft.Json` ships for
/// netstandard2.0 and runs unchanged, so moving off it is a choice somebody
/// makes rather than one the runtime makes for them.
///
/// **This is what the coverage number was being asked and could not answer.**
/// Marked against three real ports, coverage predicted the decision four times
/// out of ten, because it was never predicting the decision: it estimates how
/// much of a move is a substitution rather than a rewrite, which is a cost and
/// not a behaviour. Across those same ten, every one of the six stranded
/// packages moved and every surviving library but one stayed.
///
/// Hand-written, like every other judgement here, and absence means unknown.
/// </summary>
public sealed class Strandings
{
    private static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyDictionary<string, Stranding> _packages;

    private Strandings(IReadOnlyDictionary<string, Stranding> packages, string source)
    {
        _packages = packages;
        Source = source;
    }

    /// <summary>Where the judgements came from, so a reader can go and disagree with them.</summary>
    public string Source { get; }

    public int Count => _packages.Count;

    /// <summary>Null where nobody has recorded anything, which is not the same as "it survives".</summary>
    public Stranding? For(string package) =>
        _packages.TryGetValue(package, out var found) ? found : null;

    public static Strandings Load(string? path = null)
    {
        var file = path ?? Catalogues.Find("stranded.json");

        if (file is null || !File.Exists(file))
        {
            return new Strandings(
                new Dictionary<string, Stranding>(StringComparer.OrdinalIgnoreCase),
                file is null ? "no catalogue was found" : $"no catalogue at {file}");
        }

        try
        {
            // Loosely first: the file is hand-written and carries its reasoning
            // in keys beginning with //, which a strict read chokes on.
            var read = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(file), Reading) ?? [];

            var packages = new Dictionary<string, Stranding>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in read)
            {
                if (name.StartsWith("//", StringComparison.Ordinal)) continue;
                if (value.ValueKind != JsonValueKind.Object) continue;

                var entry = value.Deserialize<Entry>(Reading);
                if (entry is null) continue;

                packages[name] = new Stranding(entry.Strands, entry.Why ?? string.Empty);
            }

            return new Strandings(packages, file);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A catalogue that will not parse is reported as absent rather than
            // as empty: the difference decides whether anything downstream is
            // entitled to claim something.
            return new Strandings(
                new Dictionary<string, Stranding>(StringComparer.OrdinalIgnoreCase),
                $"{file} could not be read: {exception.Message}");
        }
    }

    private sealed record Entry(bool Strands, string? Why);
}
