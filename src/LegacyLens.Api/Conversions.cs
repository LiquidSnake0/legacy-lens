using LegacyLens.Analysis;

namespace LegacyLens.Api;

/// <summary>One conversion's result: the patch, and everything to read first.</summary>
public record ConversionOutcome(
    string Kind,
    string Patch,
    /// <summary>What the patch does, and what it does not handle.</summary>
    IReadOnlyList<string> Notes,
    /// <summary>What was refused, with the reason. Often the deliverable.</summary>
    IReadOnlyList<string> Refusals)
{
    public bool Empty => Patch.Length == 0;
}

/// <summary>
/// The mechanical conversions, gathered.
///
/// Nothing here writes to the tree. Every one produces a patch a person reads
/// and hands to `git apply` if they agree, which is the rule for this whole
/// milestone: the tool proposes a diff, a person approves it.
///
/// One kind at a time, deliberately. Two of these rewrite the same project
/// file, so a patch carrying both cannot apply: the second half is written
/// against text the first half already moved.
/// </summary>
public static class Conversions
{
    public static readonly IReadOnlyList<string> Kinds = ["packages", "sdk", "versions", "config"];

    public static ConversionOutcome For(string kind, string rootPath) =>
        kind.ToLowerInvariant() switch
        {
            "packages" => PackagesConfig(rootPath),
            "sdk" => SdkStyle(rootPath),
            "versions" => Versions(rootPath),
            "config" => Configuration(rootPath),
            _ => throw new ArgumentException(
                $"Unknown conversion \"{kind}\". One of: {string.Join(", ", Kinds)}."),
        };

    /* ---- the conversions ---- */

    private static ConversionOutcome PackagesConfig(string rootPath)
    {
        var survey = new Modernisation().Survey(rootPath);
        var conversion = new PackagesConfigConversion();

        var patch = new System.Text.StringBuilder();
        var notes = new List<string>();
        var converted = 0;

        foreach (var project in survey.Projects)
        {
            if (conversion.Propose(project, rootPath) is not { } proposal) continue;

            patch.Append(proposal.Patch);
            converted++;
            notes.AddRange(proposal.Caveats.Select(c => $"{proposal.Project}: {c}"));
        }

        notes.Insert(0,
            $"{converted} of {survey.Projects.Count} project(s) converted. The rest either "
            + "declare no packages, already use PackageReference, or depend on something "
            + "with no path to modern .NET.");

        return new ConversionOutcome("packages", patch.ToString(), notes, []);
    }

    private static ConversionOutcome SdkStyle(string rootPath)
    {
        var survey = new Modernisation().Survey(rootPath);
        var conversion = new SdkStyleConversion();

        var patch = new System.Text.StringBuilder();
        var notes = new List<string>();
        var refusals = new List<string>();

        foreach (var project in survey.Projects)
        {
            var verdict = conversion.Propose(project, rootPath);

            if (verdict.Proposal is { } proposal)
            {
                patch.Append(proposal.Patch);
                notes.AddRange(proposal.Caveats.Select(c => $"{proposal.Project}: {c}"));
            }
            else
            {
                refusals.Add($"{verdict.Project}: {string.Join("; ", verdict.Blockers)}");
            }
        }

        notes.Insert(0,
            $"{survey.Projects.Count - refusals.Count} converted, {refusals.Count} refused.");

        return new ConversionOutcome("sdk", patch.ToString(), notes, refusals);
    }

    private static ConversionOutcome Versions(string rootPath)
    {
        var survey = new Modernisation().Survey(rootPath);
        var unification = new PackageUnification();

        if (unification.Propose(survey, rootPath) is not { } proposal)
        {
            return new ConversionOutcome("versions", string.Empty,
            [
                $"{survey.Packages.Count} distinct package(s), none pinned to more than one "
                + "version. Nothing to unify, and it was checked.",
            ], []);
        }

        var refusals = unification.Judge(survey)
            .Where(v => v.Divergent && !v.Unifiable)
            .Select(v => $"{v.PackageId}: {string.Join("; ", v.Blockers)}")
            .ToList();

        return new ConversionOutcome("versions", proposal.Patch, proposal.Caveats, refusals);
    }

    private static ConversionOutcome Configuration(string rootPath)
    {
        var migration = new ConfigurationMigration();
        var survey = migration.Survey(rootPath);
        var proposal = migration.Propose(survey, rootPath);

        var notes = new List<string>(proposal?.Caveats ?? ["No appSettings or connectionStrings found."]);

        // Reported whether or not a patch came out. These are the reason to run
        // this against a codebase nobody has decided to port yet.
        foreach (var read in survey.Undeclared.Take(50))
        {
            notes.Add($"Read and declared nowhere: {read.Path}:{read.Line}  {read.Key}");
        }

        if (survey.Computed.Count > 0)
        {
            notes.Add(
                $"{survey.Computed.Count} read(s) use a key computed at runtime, which no "
                + "rewrite can follow. Listed rather than counted as safe.");
        }

        var types = survey.Reads
            .Where(r => r.Type is not null)
            .Select(r => r.Type!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var refusals = new List<string>();
        if (types.Count > 0)
        {
            refusals.Add(
                $"{survey.Reads.Count} call site(s) across {types.Count} type(s) still read "
                + "ConfigurationManager, and are not rewritten. "
                + ConfigurationMigration.Verdict(isStatic: false));
        }

        return new ConversionOutcome("config", proposal?.Patch ?? string.Empty, notes, refusals);
    }

    /* ---- the command ---- */

    /// <summary>
    /// The patch goes to standard output and the reasons go to standard error,
    /// so `convert . versions &gt; migration.patch` leaves a file git can take
    /// and prints what to read at the terminal.
    /// </summary>
    public static void Run(string rootPath, string[] arguments)
    {
        var full = Path.GetFullPath(rootPath);

        if (!Directory.Exists(full))
        {
            Console.Error.WriteLine($"No such directory: {full}");
            Environment.ExitCode = 1;
            return;
        }

        var kind = arguments.FirstOrDefault(a => !a.StartsWith('-'));

        // Without a kind this says what there is, rather than guessing which
        // one was meant or emitting a patch that cannot apply.
        if (kind is null)
        {
            Summarise(full);
            return;
        }

        ConversionOutcome outcome;
        try
        {
            outcome = For(kind, full);
        }
        catch (ArgumentException failure)
        {
            Console.Error.WriteLine(failure.Message);
            Environment.ExitCode = 1;
            return;
        }

        Console.Out.Write(outcome.Patch);

        foreach (var note in outcome.Notes) Console.Error.WriteLine(note);
        foreach (var refusal in outcome.Refusals) Console.Error.WriteLine($"  {refusal}");
    }

    private static void Summarise(string rootPath)
    {
        var survey = new Modernisation().Survey(rootPath);

        var packages = survey.Projects.Count(p =>
            new PackagesConfigConversion().Propose(p, rootPath) is not null);

        var sdk = survey.Projects.Count(p =>
            new SdkStyleConversion().Propose(p, rootPath).Convertible);

        var divergent = new PackageUnification().Judge(survey).Count(v => v.Unifiable);
        var config = new ConfigurationMigration().Survey(rootPath);

        Console.Out.WriteLine($"{survey.Projects.Count} project(s) under {rootPath}.");
        Console.Out.WriteLine();
        Console.Out.WriteLine($"  packages   {packages,4}  project(s) can move off packages.config");
        Console.Out.WriteLine($"  sdk        {sdk,4}  project(s) can take the SDK format");
        Console.Out.WriteLine($"  versions   {divergent,4}  package(s) are pinned to more than one version");
        Console.Out.WriteLine(
            $"  config     {config.AllAppSettings.Count + config.AllConnectionStrings.Count,4}"
            + "  setting(s) can become appsettings.json");
        Console.Out.WriteLine();
        Console.Out.WriteLine(
            $"Pass one of: {string.Join(", ", Kinds)}. Each writes a patch to standard output.");
        Console.Out.WriteLine(
            "One at a time on purpose: packages and sdk both rewrite the project file, "
            + "so a patch carrying both cannot apply.");
    }
}
