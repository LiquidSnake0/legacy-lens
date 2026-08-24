using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LegacyLens.Analysis;

/// <summary>One place the code reads configuration, and what it reads.</summary>
public record ConfigurationRead(
    string Path,
    int Line,
    /// <summary>The type the call sits in, or null at file scope.</summary>
    string? Type,
    /// <summary>AppSettings or ConnectionStrings.</summary>
    string Kind,
    /// <summary>The key, when it is a literal. Null when it is computed.</summary>
    string? Key)
{
    public bool Literal => Key is not null;
}

/// <summary>What one settings file declares.</summary>
public record ConfigurationFile(
    string Path,
    IReadOnlyDictionary<string, string> AppSettings,
    IReadOnlyDictionary<string, string> ConnectionStrings);

public record ConfigurationSurvey(
    IReadOnlyList<ConfigurationFile> Files,
    IReadOnlyList<ConfigurationRead> Reads)
{
    public IReadOnlyDictionary<string, string> AllAppSettings => Merge(f => f.AppSettings);
    public IReadOnlyDictionary<string, string> AllConnectionStrings => Merge(f => f.ConnectionStrings);

    /// <summary>
    /// Keys the code reads that no config file declares.
    ///
    /// The finding worth having. Each one is a null the application meets at
    /// runtime, and it was already there before anyone thought about porting.
    /// </summary>
    public IReadOnlyList<ConfigurationRead> Undeclared =>
        Reads.Where(r => r.Literal)
            .Where(r => !(r.Kind == "AppSettings"
                ? AllAppSettings.ContainsKey(r.Key!)
                : AllConnectionStrings.ContainsKey(r.Key!)))
            .ToList();

    /// <summary>Keys declared that nothing reads. Dead weight, carried forward otherwise.</summary>
    public IReadOnlyList<string> Unread
    {
        get
        {
            var read = Reads.Where(r => r.Literal && r.Kind == "AppSettings")
                .Select(r => r.Key!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return AllAppSettings.Keys.Where(k => !read.Contains(k)).OrderBy(k => k).ToList();
        }
    }

    /// <summary>Reads whose key is computed, which no rewrite can follow.</summary>
    public IReadOnlyList<ConfigurationRead> Computed =>
        Reads.Where(r => !r.Literal).ToList();

    private Dictionary<string, string> Merge(
        Func<ConfigurationFile, IReadOnlyDictionary<string, string>> pick)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Files)
        {
            foreach (var (key, value) in pick(file)) merged[key] = value;
        }

        return merged;
    }
}

/// <summary>
/// `ConfigurationManager` to `IConfiguration`.
///
/// The half that is mechanical is the settings themselves: an `appSettings`
/// block and a `connectionStrings` block become one `appsettings.json`, and
/// nothing about that transformation requires a judgement.
///
/// The half that is not mechanical is the call sites. `ConfigurationManager`
/// is a static reachable from anywhere, and `IConfiguration` is a dependency
/// somebody has to hand in. Turning one into the other means opening a seam in
/// every type that reads configuration, and every caller of those types has to
/// change with it. A tool that rewrote them anyway would produce a patch that
/// does not compile, which is worse than no patch.
///
/// So this converts the settings, and reports the calls: where they are, what
/// they read, and which of them read a key nothing declares. That last list is
/// usually the reason to run it.
/// </summary>
public class ConfigurationMigration
{
    private static readonly string[] SkipDirectories =
        [".git", "bin", "obj", "node_modules", "packages", ".vs"];

    public ConfigurationSurvey Survey(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        var files = new List<ConfigurationFile>();
        var reads = new List<ConfigurationRead>();

        foreach (var path in Walk(rootPath))
        {
            var name = Path.GetFileName(path);

            if (name.Equals("web.config", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("app.config", StringComparison.OrdinalIgnoreCase))
            {
                if (Read(path) is { } file) files.Add(file);
            }
            else if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                reads.AddRange(ReadsIn(path));
            }
        }

        return new ConfigurationSurvey(files, reads);
    }

    /// <summary>
    /// The settings, as one `appsettings.json`, emitted as a patch.
    ///
    /// Null when there is nothing to carry over. The values are copied
    /// verbatim: this is a translation, and a translation that improves on its
    /// source is a different file with the same name.
    /// </summary>
    public ConversionProposal? Propose(ConfigurationSurvey survey, string rootPath)
    {
        var settings = survey.AllAppSettings;
        var connections = survey.AllConnectionStrings;

        if (settings.Count == 0 && connections.Count == 0) return null;

        var root = new JsonObject();

        foreach (var (key, value) in settings.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            // Kept exactly as it was written, dots and colons and all.
            //
            // Nesting "Mail.Host" into { "Mail": { "Host": ... } } is what a
            // person would write by hand, and it is wrong here: .NET joins
            // nested names with a colon, so the key becomes "Mail:Host" and
            // every call site reading "Mail.Host" gets null. Flat, the key
            // survives and the reads keep working. Restructuring is a change
            // of keys, which is a change of code, and belongs to whoever is
            // reading this patch rather than to the patch.
            root[key] = value;
        }

        if (connections.Count > 0)
        {
            var block = new JsonObject();
            foreach (var (name, value) in connections.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                block[name] = value;
            }

            root["ConnectionStrings"] = block;
        }

        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // The values are connection strings and URLs. Escaping every
            // ampersand into & produces a file that is valid, unreadable,
            // and impossible to diff against the original by eye.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";

        var caveats = new List<string>
        {
            $"{settings.Count} app setting(s) and {connections.Count} connection string(s), "
            + $"read from {survey.Files.Count} config file(s).",

            "Keys are kept flat and unchanged, so configuration[\"Mail.Host\"] reads what "
            + "ConfigurationManager.AppSettings[\"Mail.Host\"] read. Nesting them reads "
            + "better and renames them: .NET joins nested names with a colon, and every "
            + "call site would have to move with them.",
        };

        if (connections.Count > 0 && connections.Any(c => LooksLikeSecret(c.Value)))
        {
            caveats.Add(
                "At least one connection string carries a password. It is copied "
                + "as it was found, because a translation that edits its source is "
                + "a different file. Move it to user secrets or the environment "
                + "before this is committed anywhere.");
        }

        var duplicated = Duplicated(survey);
        if (duplicated.Count > 0)
        {
            caveats.Add(
                $"{duplicated.Count} key(s) are declared in more than one config file with "
                + "different values, and the last one read wins here: "
                + string.Join(", ", duplicated.Take(5))
                + (duplicated.Count > 5 ? ", and more" : "")
                + ". Which one was right at runtime depended on which application "
                + "loaded them, and that is not something these files record.");
        }

        if (survey.Undeclared.Count > 0)
        {
            caveats.Add(
                $"{survey.Undeclared.Count} key(s) are read by the code and declared "
                + "nowhere. They are not in this file either, because inventing a "
                + "value is the one thing that would make it wrong.");
        }

        return new ConversionProposal(
            "appsettings.json", UnifiedDiff.Creating("appsettings.json", json), caveats);
    }

    /// <summary>
    /// Whether a type can take an <c>IConfiguration</c> without a rewrite.
    ///
    /// Deliberately the same shape as the seam survey in M10, because it is
    /// the same question: a static class has no constructor to hand anything
    /// to, and every other type has callers that change with it.
    /// </summary>
    public static string Verdict(bool isStatic) =>
        isStatic
            ? "Static, so there is no constructor to hand an IConfiguration to. "
            + "The call sites move to a parameter, or the class stops being static."
            : "Takes an IConfiguration through its constructor, which every caller "
            + "of it then has to supply. Mechanical, but not local.";

    private static bool LooksLikeSecret(string value) =>
        value.Contains("password=", StringComparison.OrdinalIgnoreCase)
        || value.Contains("pwd=", StringComparison.OrdinalIgnoreCase);

    private static List<string> Duplicated(ConfigurationSurvey survey)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clashing = new List<string>();

        foreach (var file in survey.Files)
        {
            foreach (var (key, value) in file.AppSettings)
            {
                if (seen.TryGetValue(key, out var previous) && previous != value)
                {
                    if (!clashing.Contains(key, StringComparer.OrdinalIgnoreCase)) clashing.Add(key);
                }

                seen[key] = value;
            }
        }

        return clashing;
    }

    private static ConfigurationFile? Read(string path)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (Exception exception)
            when (exception is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var connections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var add in document.Descendants("appSettings").Elements("add"))
        {
            var key = add.Attribute("key")?.Value;
            if (key is not null) settings[key] = add.Attribute("value")?.Value ?? string.Empty;
        }

        foreach (var add in document.Descendants("connectionStrings").Elements("add"))
        {
            var name = add.Attribute("name")?.Value;
            if (name is not null)
                connections[name] = add.Attribute("connectionString")?.Value ?? string.Empty;
        }

        return settings.Count == 0 && connections.Count == 0
            ? null
            : new ConfigurationFile(path, settings, connections);
    }

    /// <summary>
    /// Every ConfigurationManager read in one file.
    ///
    /// Matched on syntax rather than on resolved symbols, for the same reason
    /// the rest of this analysis is: requiring the solution to compile would
    /// give up the one property that makes it usable on inherited code.
    /// </summary>
    private static IEnumerable<ConfigurationRead> ReadsIn(string path)
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

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(source).GetRoot();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var owner = access.Expression switch
            {
                IdentifierNameSyntax bare => bare.Identifier.Text,
                MemberAccessExpressionSyntax nested => nested.Name.Identifier.Text,
                _ => null,
            };

            if (owner != "ConfigurationManager") continue;

            var kind = access.Name.Identifier.Text;
            if (kind is not ("AppSettings" or "ConnectionStrings")) continue;

            yield return new ConfigurationRead(
                path,
                access.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                access.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text,
                kind,
                KeyOf(access));
        }
    }

    /// <summary>
    /// The literal key an indexer reads, or null when it is computed.
    ///
    /// `ConfigurationManager.AppSettings[Name]` is not something a rewrite can
    /// follow, and reporting it as unknown is the honest answer.
    /// </summary>
    private static string? KeyOf(MemberAccessExpressionSyntax access) =>
        access.Parent is ElementAccessExpressionSyntax element
        && element.ArgumentList.Arguments.Count == 1
        && element.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

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
            else
            {
                yield return entry;
            }
        }
    }
}
