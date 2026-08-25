using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegacyLens.Analysis;

/// <summary>Something a framework offered and stopped offering, and what to do instead.</summary>
public record Feature(
    string Name,
    IReadOnlyList<string> Types,
    /// <summary>What the old framework did.</summary>
    string Was,
    /// <summary>What the new one does, or that it does not.</summary>
    string Now,
    /// <summary>What a person chooses between. Never a recommendation.</summary>
    IReadOnlyList<string> Options);

/// <summary>One feature, and the types this codebase actually reached it through.</summary>
public record Touched(Feature Feature, IReadOnlyList<ApiUse> Through)
{
    public int Uses => Through.Sum(t => t.Uses);
}

/// <summary>
/// What a framework stopped offering, grouped by the thing it offered.
///
/// **The unit of decision is the feature, not the type.** M26 measured it: two
/// attributes that turn off request validation were 596 of the 857 calls the
/// tool reported as types modern .NET does not have, and one sentence answered
/// all of them. What was left came to forty-four types, and forty-four rows is
/// not forty-four decisions. `ScriptBundle`, `StyleBundle`,
/// `CssRewriteUrlTransform` and `IItemTransform` are one question about
/// bundling. `HttpContextBase`, `HttpRequestBase`, `RequestContext` and
/// `HttpPostedFileBase` are one question about the request context.
///
/// So the catalogue answers per feature, and a codebase's forty-four unknowns
/// become six questions with their options attached.
///
/// **The options are not advice.** Which one is right depends on how many
/// people, how much time, whether the old and the new have to run side by side:
/// none of that is in the code, and a tool that recommended one would be
/// pretending it knew. They are written so somebody can choose between them,
/// which is the most this can honestly do.
/// </summary>
public sealed class Features
{
    private static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyDictionary<string, IReadOnlyList<Feature>> _packages;

    private Features(IReadOnlyDictionary<string, IReadOnlyList<Feature>> packages, string source)
    {
        _packages = packages;
        Source = source;
    }

    public string Source { get; }

    public int Count => _packages.Values.Sum(f => f.Count);

    public IReadOnlyList<Feature> For(string package) =>
        _packages.TryGetValue(package, out var found) ? found : [];

    /// <summary>
    /// The questions these types come to, largest first, and nothing about the
    /// ones no feature covers.
    ///
    /// A type belonging to no feature is left out rather than given one of its
    /// own. The gap is real and saying so is the point of the count beside it:
    /// on nopCommerce twenty-one of the forty-four are third-party names the
    /// syntax attributed to the wrong package, and inventing a feature for them
    /// would bury that.
    /// </summary>
    public IReadOnlyList<Touched> Ask(string package, IReadOnlyList<ApiUse> types)
    {
        var byName = types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        return For(package)
            .Select(feature => new Touched(
                feature,
                feature.Types
                    .Where(byName.ContainsKey)
                    .Select(name => byName[name])
                    .OrderByDescending(use => use.Uses)
                    .ToList()))
            .Where(touched => touched.Through.Count > 0)
            .OrderByDescending(touched => touched.Uses)
            .ToList();
    }

    public static Features Load(string? path = null)
    {
        var file = path ?? Catalogues.Find("features.json");

        if (file is null || !File.Exists(file))
        {
            return new Features(
                new Dictionary<string, IReadOnlyList<Feature>>(StringComparer.OrdinalIgnoreCase),
                file is null ? "no catalogue was found" : $"no catalogue at {file}");
        }

        try
        {
            // Loosely first: hand-written, and it carries its reasoning in keys
            // beginning with //.
            var read = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(file), Reading) ?? [];

            var packages = new Dictionary<string, IReadOnlyList<Feature>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in read)
            {
                if (name.StartsWith("//", StringComparison.Ordinal)) continue;
                if (value.ValueKind != JsonValueKind.Array) continue;

                packages[name] = value.Deserialize<List<Feature>>(Reading) ?? [];
            }

            return new Features(packages, file);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new Features(
                new Dictionary<string, IReadOnlyList<Feature>>(StringComparer.OrdinalIgnoreCase),
                $"{file} could not be read: {exception.Message}");
        }
    }
}
