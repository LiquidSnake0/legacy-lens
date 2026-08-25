using System.Text;
using System.Text.RegularExpressions;
using LegacyLens.Analysis;
using LegacyLens.Characterization;

namespace LegacyLens.Api.Generation;

/// <summary>One file, before and after, and what the compiler said.</summary>
public record ProjectionResult(
    string Path,
    string Package,
    string Before,
    string After,
    ProjectionVerdict Verdict,
    /// <summary>How many times the model was asked. Two means the first try failed.</summary>
    int Attempts,
    /// <summary>Correspondences from the catalogue that were handed to it.</summary>
    IReadOnlyList<string> Given,
    IReadOnlyList<string> Notes,
    /// <summary>
    /// What both versions did when called with the same values, when this
    /// server is allowed to call them at all. Null means it was not run, and
    /// the reason is in the notes.
    /// </summary>
    EquivalenceReport? Behaviour = null,
    /// <summary>
    /// Why there is no behaviour report, when there is none.
    ///
    /// Carried as its own field rather than left for a reader to recognise
    /// among the notes. A caller matching on the wording is a caller that
    /// silently stops working the day the wording improves.
    /// </summary>
    string? BehaviourRefusal = null)
{
    /// <summary>
    /// Everything this projection is allowed to say, with both checks counted.
    ///
    /// The compiler's sentence ends in a disclaimer about behaviour, because
    /// the compiler cannot see any. Where a run has compared the two versions,
    /// its sentence takes that place: a projection whose behaviour was checked
    /// should not still be carrying a note saying it was not, and a reader
    /// following only this field should never be told less than was measured.
    /// </summary>
    public string Claim => Behaviour is null
        ? Verdict.Claim
        : Verdict.Claim.Replace(ProjectionVerdict.Unverified, Behaviour.Claim,
            StringComparison.Ordinal);
}

/// <summary>
/// A rewritten file, compiled before anyone is shown it.
///
/// This is where the three pieces meet. The usage surface says which types a
/// file touches, the catalogue says what each becomes, and those
/// correspondences are handed to the model as facts rather than left for it to
/// remember. Then the compiler decides whether the result is worth showing.
///
/// The model is not asked what replaces what. That question has a written
/// answer, and asking a model for it is how references to packages that do not
/// exist get into a migration. It is asked to apply correspondences it was
/// given to code it was given, which is the one part of this no catalogue can
/// do: every file uses them differently.
///
/// What comes out claims exactly one thing: it compiles. Not that it behaves
/// the same, which needs the characterization net and is a larger promise.
/// </summary>
public class Projections
{
    /// <summary>
    /// How many times a failure is handed back with its errors.
    ///
    /// Two. A model that cannot fix a resolution error told exactly which name
    /// failed is not going to find it on the fifth attempt, and each one costs
    /// tens of seconds on a local model.
    /// </summary>
    private const int Attempts = 2;

    /// <summary>
    /// Above this, a projection is refused rather than attempted.
    ///
    /// Measured: Orchard's heaviest ASP.NET MVC file is 821 lines and outran
    /// the patience for it on a local model, twice over because a failure is
    /// retried.
    /// </summary>
    private const int TooLong = 400;

    private static readonly Regex Fence = new(
        @"^\s*```(?:csharp|cs|c#)?\s*\n(.*?)\n\s*```\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly IChatClients _chats;
    private readonly Execution _execution;
    private readonly ILogger<Projections> _log;

    public Projections(IChatClients chats, Execution execution, ILogger<Projections> log)
    {
        _chats = chats;
        _execution = execution;
        _log = log;
    }

    public async Task<ProjectionResult> ProjectAsync(
        string path,
        string package,
        string? root = null,
        ModelChoice? model = null,
        CancellationToken ct = default)
    {
        var before = await File.ReadAllTextAsync(path, ct);

        // Refused rather than attempted. A file this long takes a local model
        // minutes per attempt and produces something nobody reads in a browser,
        // and the rewrite it demonstrates is the same one a short file shows.
        var lines = before.AsSpan().Count('\n') + 1;
        if (lines > TooLong)
        {
            return new ProjectionResult(
                path, package, before, string.Empty,
                new ProjectionVerdict(false, Projection.Target, [], [], [], []),
                0, [],
                [
                    $"{lines} lines, and the limit is {TooLong}. Projecting it would take a "
                    + "local model minutes per attempt to produce something nobody reads in a "
                    + "browser. Pick a shorter file using the same package: the rewrite it "
                    + "demonstrates is the same one.",
                ]);
        }

        // What the solution declares, so a name from the project can be told
        // from a name that was made up. Without it every unresolved type looks
        // invented, and a real controller names a dozen of its own project's.
        IReadOnlySet<string>? declared = null;
        IReadOnlySet<string>? namespaces = null;

        if (root is { Length: > 0 } && Directory.Exists(root))
        {
            var map = new SolutionAnalysis().Types(root);

            declared = map.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

            // The namespaces too. A file compiled outside its project fails on
            // `Orchard.ContentManagement` before it fails on anything in it.
            namespaces = map.Types
                .Select(t => t.Namespace)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
        }

        var catalogue = Successors.Load();
        var candidate = catalogue.For(package).FirstOrDefault();

        var notes = new List<string>();
        var given = new List<string>();

        if (candidate is null)
        {
            notes.Add(
                $"No successor is catalogued for {package}, so the model was given the file "
                + "and nothing else. Whatever it produces is its own recollection, which is "
                + "the situation this tool exists to avoid.");
        }
        else
        {
            // Only the ones this file actually uses. Handing over the whole
            // catalogue buries the six lines that matter in ninety.
            foreach (var (from, to) in candidate.Types)
            {
                if (!Mentions(before, from)) continue;

                given.Add(to is null
                    ? $"{from}: nothing replaces it"
                    : $"{from} becomes {to}");
            }

            if (given.Count == 0)
            {
                notes.Add(
                    "None of the catalogued types appear in this file, so there was nothing "
                    + "to hand over beyond the file itself.");
            }
        }

        var chat = _chats.For(model);
        var after = string.Empty;
        ProjectionVerdict verdict = new(
            false, Projection.Target, ["Nothing was produced."], [], [], []);
        var compiler = new Projection();

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var prompt = attempt == 1
                ? First(before, package, candidate?.Package, given)
                : Again(after, verdict);

            after = Unfence(await chat.CompleteAsync(prompt, ct));
            verdict = compiler.Compile(after, declared, namespaces);

            // Sound, not Compiles. A file compiled outside its project cannot
            // resolve its project, and demanding that it does would reject
            // every projection worth making. What must not happen is a name
            // that exists nowhere.
            if (verdict.Sound)
            {
                // Only now, and only on something that compiles and invents
                // nothing. Running a rewrite that names types which do not
                // exist would fail for a reason already known, and running one
                // at all is a decision the operator makes rather than this
                // method.
                var behaviour = Behaviour(before, after, notes);

                return new ProjectionResult(
                    path, package, before, after, verdict, attempt, given, notes, behaviour,
                    behaviour is null ? _execution.Refusal : null);
            }

            _log.LogInformation(
                "Projection of {Path} invented names on attempt {Attempt}: {Invented}",
                path, attempt, string.Join(", ", verdict.Invented));
        }

        notes.Add(
            $"After {Attempts} attempts it still names things that exist nowhere, so it is "
            + "shown as a failure rather than as a migration. Inventing a type is the one "
            + "thing this refuses to hand over quietly.");

        return new ProjectionResult(
            path, package, before, after, verdict, Attempts, given, notes);
    }

    /// <summary>
    /// What both versions do, when this server is allowed to find out.
    ///
    /// The step this milestone exists for. Everything up to here proves the
    /// rewrite is valid code; only calling both versions with the same values
    /// says whether it still does the same thing. On a file whose work happens
    /// through a web framework it will compare nothing and say so, which is
    /// the honest answer rather than a missing one.
    /// </summary>
    private EquivalenceReport? Behaviour(string before, string after, List<string> notes)
    {
        if (!_execution.Allowed)
        {
            notes.Add(_execution.Refusal);
            return null;
        }

        var report = new Equivalence().Compare(before, after);

        notes.Add(report.Claim);

        return report;
    }

    /// <summary>
    /// Whether a file names a type at all, cheaply.
    ///
    /// Word-bounded rather than a substring: `View` matching inside
    /// `ViewModelBuilder` would hand over a correspondence the file never uses,
    /// which is noise in the one place noise is expensive.
    /// </summary>
    private static bool Mentions(string source, string type) =>
        Regex.IsMatch(source, $@"\b{Regex.Escape(type)}\b");

    private static string First(
        string before, string package, string? successor, IReadOnlyList<string> given)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine(
            "Rewrite this C# file so it compiles on modern .NET, moving it off "
            + package + (successor is { Length: > 0 } ? $" and onto {successor}." : "."));
        prompt.AppendLine();

        if (given.Count > 0)
        {
            // Facts, not suggestions. The model is not being asked what
            // replaces what; that question has a written answer.
            prompt.AppendLine("These correspondences are established. Use them exactly:");
            foreach (var line in given) prompt.AppendLine($"  {line}");
            prompt.AppendLine();
            prompt.AppendLine(
                "Where a correspondence says nothing replaces it, leave a // TODO naming "
                + "what was lost rather than inventing a substitute.");
            prompt.AppendLine();
        }

        prompt.AppendLine(
            "Return only the rewritten file. No explanation, no markdown fence. "
            + "Do not invent types, methods or packages: anything you are unsure of, "
            + "leave as a // TODO. Keep the class and member names as they are.");
        prompt.AppendLine();
        prompt.AppendLine("--- the file ---");
        prompt.AppendLine(before);

        return prompt.ToString();
    }

    private static string Again(string attempt, ProjectionVerdict verdict)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine(
            "This names things that do not exist. Fix it and return only the corrected file.");
        prompt.AppendLine();

        if (verdict.Invented.Count > 0)
        {
            prompt.AppendLine(
                "These names exist nowhere: not in the framework, not in this solution. "
                + "They were invented, so remove them or replace them with something real:");
            foreach (var name in verdict.Invented.Take(10)) prompt.AppendLine($"  {name}");
            prompt.AppendLine();
        }

        if (verdict.Unimported.Count > 0)
        {
            // These exist. Telling the model to invent a replacement for them
            // is how a correct name gets thrown away on the second attempt.
            prompt.AppendLine(
                "These exist in the target framework and are only missing a using. "
                + "Add the namespace rather than changing the name:");
            foreach (var name in verdict.Unimported.Take(10)) prompt.AppendLine($"  {name}");
            prompt.AppendLine();
        }

        prompt.AppendLine("The compiler said:");
        foreach (var error in verdict.Errors.Take(10)) prompt.AppendLine($"  {error}");
        prompt.AppendLine();
        prompt.AppendLine("--- what you wrote ---");
        prompt.AppendLine(attempt);

        return prompt.ToString();
    }

    /// <summary>
    /// Takes the code out of a markdown fence.
    ///
    /// The prompt asks for none, and models add one anyway. A fence reaching the
    /// compiler is a syntax error that looks like the projection failed, when
    /// what failed was following an instruction.
    /// </summary>
    internal static string Unfence(string answer)
    {
        var match = Fence.Match(answer.Trim());
        return match.Success ? match.Groups[1].Value : answer.Trim();
    }
}
