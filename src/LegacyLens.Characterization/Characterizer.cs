using System.Reflection;

namespace LegacyLens.Characterization;

/// <summary>Everything one run produced, including what it refused.</summary>
public record CharacterizationRun(
    string Assembly,
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<Skipped> Skipped,
    int MethodsConsidered,
    int CallsMade,
    long ElapsedMs)
{
    public int Tests => Files.Sum(f => f.Cases);

    /// <summary>
    /// Why the run refused what it refused, largest group first.
    ///
    /// This is the honest headline of a characterization run. Four tests from
    /// six hundred methods is either a triumph or a dead end depending entirely
    /// on what stopped the other five hundred and ninety six.
    /// </summary>
    public IReadOnlyList<(SkipReason Reason, int Count)> Refusals =>
        Skipped.GroupBy(s => s.Reason)
               .Select(g => (g.Key, g.Count()))
               .OrderByDescending(entry => entry.Item2)
               .ToList();
}

/// <summary>A test file that compiled and passed, ready to be written out.</summary>
public record GeneratedFile(string FileName, string Source, int Cases);

/// <summary>
/// Records what an assembly's methods do, as tests that are known to pass.
///
/// The order of operations is the whole argument. Call the code, watch it
/// twice, write the observation down as an assertion, compile it, run it, and
/// keep only what survived. Anything that fails at any step is dropped with a
/// reason rather than shown to somebody as a suggestion.
/// </summary>
public class Characterizer
{
    /// <summary>Cases attempted per method.</summary>
    public int CasesPerMethod { get; init; } = 4;

    /// <summary>
    /// Methods attempted in one run. A cap rather than a completeness claim:
    /// exceeded, it is reported rather than silently applied.
    /// </summary>
    public int MethodLimit { get; init; } = 200;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Which types to look at. Left unset, every public type in the assembly is
    /// considered, which is rarely what anyone wants: the net belongs under the
    /// files that were ranked dangerous, not under all of them.
    /// </summary>
    public Func<Type, bool>? Types { get; init; }

    public CharacterizationRun Run(Assembly subject)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var (targets, skipped) = new TargetFinder().Find(subject, Types);
        var refused = skipped.ToList();
        var observer = new Observer { Timeout = Timeout };

        var considered = targets.Count;
        var calls = 0;
        var characterized = new List<Characterized>();

        foreach (var target in targets.Take(MethodLimit))
        {
            var parameters = target.Method.GetParameters().Select(p => p.ParameterType).ToList();
            var observations = new List<Observation>();

            object? instance = null;
            if (!target.Method.IsStatic)
            {
                try
                {
                    instance = Activator.CreateInstance(target.Method.DeclaringType!);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A parameterless constructor that throws is a dependency
                    // the type needs and this tool cannot supply.
                    refused.Add(new Skipped(target.Display, SkipReason.NotConstructible,
                        exception.GetType().Name));
                    continue;
                }
            }

            foreach (var arguments in Values.Cases(parameters, CasesPerMethod))
            {
                calls++;
                var (observation, reason) = observer.Observe(target.Method, instance, arguments);

                if (observation is not null) observations.Add(observation);
                else if (reason is not null) refused.Add(new Skipped(target.Display, reason.Value));
            }

            if (observations.Count > 0)
                characterized.Add(new Characterized(target, observations));
        }

        var files = new List<GeneratedFile>();

        foreach (var group in characterized.GroupBy(c => c.Target.Method.DeclaringType!))
        {
            var file = Emit(group.Key, group.ToList(), subject, refused);
            if (file is not null) files.Add(file);
        }

        return new CharacterizationRun(
            subject.GetName().Name ?? "unknown",
            files,
            refused,
            considered,
            calls,
            started.ElapsedMilliseconds);
    }

    /// <summary>
    /// Writes a file, checks it, and if anything failed, drops those cases and
    /// checks what is left.
    ///
    /// One retry rather than a loop. A first round of failures is expected: a
    /// method whose result serialises differently under the test's culture, an
    /// assertion on a value that does not compare the way it printed. A second
    /// round of failures means something is wrong with this tool rather than
    /// with any individual case, and quietly dropping cases until the file
    /// passes would hide exactly that.
    /// </summary>
    private GeneratedFile? Emit(
        Type subject, List<Characterized> methods, Assembly assembly, List<Skipped> refused)
    {
        var writer = new TestWriter { GeneratedAt = GeneratedAt };
        var verifier = new Verifier();

        var suite = writer.Write(subject, methods);
        var verification = verifier.Verify(suite, assembly);

        if (verification.Clean)
            return new GeneratedFile($"{subject.Name}Characterization.cs", suite.Source, suite.Cases.Count);

        if (!verification.Compiled)
        {
            refused.Add(new Skipped(subject.FullName ?? subject.Name,
                SkipReason.FailedItsOwnCheck,
                verification.CompilerErrors.FirstOrDefault() ?? "did not compile"));
            return null;
        }

        var failing = verification.Failed.Keys.ToHashSet(StringComparer.Ordinal);
        var survivors = new List<Characterized>();

        foreach (var method in methods)
        {
            var kept = suite.Cases
                .Where(c => c.Target == method.Target && !failing.Contains(c.TestName))
                .Select(c => c.Observation)
                .ToList();

            var dropped = method.Observations.Count - kept.Count;
            for (var i = 0; i < dropped; i++)
                refused.Add(new Skipped(method.Target.Display, SkipReason.FailedItsOwnCheck));

            if (kept.Count > 0) survivors.Add(method with { Observations = kept });
        }

        if (survivors.Count == 0) return null;

        var second = writer.Write(subject, survivors);
        var recheck = verifier.Verify(second, assembly);

        if (!recheck.Clean)
        {
            refused.Add(new Skipped(subject.FullName ?? subject.Name,
                SkipReason.FailedItsOwnCheck, "still failing after dropping the failures"));
            return null;
        }

        return new GeneratedFile(
            $"{subject.Name}Characterization.cs", second.Source, second.Cases.Count);
    }
}
