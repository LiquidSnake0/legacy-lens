using System.Diagnostics;
using System.Reflection;

namespace LegacyLens.Characterization;

/// <summary>One call that did not do the same thing twice.</summary>
public record Divergence(string Arguments, string Before, string After);

/// <summary>One method, called the same way in both versions.</summary>
public record Compared(
    string Type,
    string Method,
    string Signature,
    int Cases,
    IReadOnlyList<Divergence> Divergences,
    /// <summary>Something true about the pair that is not a divergence, such as a changed return type.</summary>
    string? Note = null)
{
    public bool Matched => Divergences.Count == 0;
}

/// <summary>
/// What was compared, what moved, and what was never looked at.
///
/// The third of those is the one that has to be read. A report saying eleven
/// methods matched, with nothing about the forty that were passed over, is the
/// sentence that gets a rewrite signed off.
/// </summary>
public record EquivalenceReport(
    bool Ran,
    IReadOnlyList<string> BeforeErrors,
    IReadOnlyList<string> AfterErrors,
    IReadOnlyList<Compared> Methods,
    IReadOnlyList<Skipped> Skipped,
    long ElapsedMs)
{
    public IReadOnlyList<Compared> Moved => Methods.Where(m => !m.Matched).ToList();

    public int Cases => Methods.Sum(m => m.Cases);

    /// <summary>
    /// True only when something was actually compared and none of it moved.
    ///
    /// Zero methods compared is not success. It is the most likely outcome on a
    /// file whose work happens through a framework, and reporting it as a pass
    /// would be the worst lie this tool could tell.
    /// </summary>
    public bool Verified => Ran && Methods.Count > 0 && Moved.Count == 0;

    /// <summary>Why the run refused what it refused, largest group first.</summary>
    public IReadOnlyList<(SkipReason Reason, int Count)> Refusals =>
        Skipped.GroupBy(s => s.Reason)
               .Select(g => (g.Key, g.Count()))
               .OrderByDescending(entry => entry.Item2)
               .ToList();

    /// <summary>
    /// Exactly what was established, in one sentence, and never more than that.
    ///
    /// Written here rather than left to each caller so there is one place where
    /// the claim can be checked against what the run actually did.
    /// </summary>
    public string Claim
    {
        get
        {
            if (!Ran)
            {
                return BeforeErrors.Count > 0
                    ? "Nothing was checked: the original does not compile in this runtime, so "
                      + "there is no behaviour to record."
                    : "Nothing was checked: the rewrite does not compile in this runtime.";
            }

            if (Methods.Count == 0)
            {
                return $"Nothing was compared. {Skipped.Count} method(s) were passed over, and "
                     + "the reasons are listed: this file's behaviour is not reachable by calling "
                     + "it with invented values.";
            }

            var scope = $"{Methods.Count} method(s) over {Cases} call(s)";

            return Moved.Count == 0
                ? $"{scope} returned the same thing in both versions. Nothing else in the file "
                  + "was checked, and the methods that were passed over are listed."
                : $"{Moved.Count} of {scope} returned something different. The calls that "
                  + "disagree are listed with the values that produced them.";
        }
    }
}

/// <summary>
/// Runs both versions of a file and compares what they did.
///
/// This is the step the projection has been missing. A projection that compiles
/// and invents nothing is a claim about the code being valid, not about it
/// doing the same thing, and the gap between those two is where a migration
/// goes wrong quietly. Here the same values go into both versions and the
/// results are compared, so the claim can become *and nothing moved* on the
/// part that was actually called.
///
/// Three things keep it from overclaiming.
///
/// **The same values, not equivalent ones.** One set of arguments is built and
/// handed to both, so a difference is the code's and not the input's. That is
/// also why parameter types the file declares itself are refused: an object
/// built in one version cannot be passed to the other, and rebuilding it from a
/// recipe would be comparing two objects rather than one.
///
/// **A method that disagrees with itself is dropped, not reported.** Both calls
/// go through the same observer as the characterization net, which runs each
/// one twice and keeps it only if the two runs agree. Without that, every
/// method reading a clock would be reported as a behaviour change.
///
/// **What was not compared is counted and named.** On a file whose work happens
/// through a web framework this compares nothing at all, and says so.
/// </summary>
public class Equivalence
{
    /// <summary>
    /// Cases attempted per method.
    ///
    /// Higher than the characterization net's, and for a different reason. That
    /// one writes a test file somebody has to read, so every case costs a
    /// reader. This one prints a verdict, so a case costs four calls into pure
    /// code, and the numbers the file itself mentions need room after the
    /// invented ones.
    /// </summary>
    public int CasesPerMethod { get; init; } = 14;

    /// <summary>How long one call is given before it is abandoned.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Methods attempted in one run.
    ///
    /// A cap rather than a completeness claim: exceeded, the rest are recorded
    /// as passed over so the number of methods compared never reads as the
    /// number of methods there were.
    /// </summary>
    public int MethodLimit { get; init; } = 200;

    /// <summary>Divergent calls kept per method. Enough to diagnose, not to scroll.</summary>
    private const int ShownPerMethod = 5;

    public EquivalenceReport Compare(string before, string after)
    {
        var started = Stopwatch.StartNew();

        using var original = Sandbox.Compile(before, "before");

        if (!original.Loaded)
            return new EquivalenceReport(false, original.Errors, [], [], [], started.ElapsedMilliseconds);

        using var rewritten = Sandbox.Compile(after, "after");

        if (!rewritten.Loaded)
            return new EquivalenceReport(false, [], rewritten.Errors, [], [], started.ElapsedMilliseconds);

        var (targets, skipped) = new TargetFinder().Find(original.Assembly!);
        var refused = skipped.ToList();

        // The numbers and strings the original mentions, offered back to it as
        // arguments. Read from the original rather than the rewrite: the
        // question is whether the rewrite still agrees at the boundaries the
        // code already had, and a rewrite that moved one would otherwise be
        // asked only about its own.
        var mentioned = Literals.From(before);

        var counterparts = Counterparts(rewritten.Assembly!);
        var observer = new Observer { Timeout = Timeout };
        var compared = new List<Compared>();

        foreach (var target in targets.Take(MethodLimit))
        {
            var outcome = CompareOne(target, counterparts, observer, mentioned, refused);
            if (outcome is not null) compared.Add(outcome);
        }

        foreach (var beyond in targets.Skip(MethodLimit))
            refused.Add(new Skipped(beyond.Display, SkipReason.BeyondTheLimit));

        return new EquivalenceReport(
            true, [], [], compared, refused, started.ElapsedMilliseconds);
    }

    private Compared? CompareOne(
        Target target,
        IReadOnlyDictionary<string, Type> counterparts,
        Observer observer,
        IReadOnlyDictionary<Type, IReadOnlyList<object?>> mentioned,
        List<Skipped> refused)
    {
        var method = target.Method;
        var parameters = method.GetParameters().Select(p => p.ParameterType).ToList();

        // The values have to survive the trip to the other assembly. A type the
        // file declares itself is a different type over there even when the
        // source is identical, so an object built here cannot be passed there.
        var unportable = parameters.Where(p => !Portable(p)).Select(p => p.Name).Distinct().ToList();

        if (unportable.Count > 0)
        {
            refused.Add(new Skipped(target.Display, SkipReason.ArgumentNotPortable,
                string.Join(", ", unportable)));
            return null;
        }

        var declaring = method.DeclaringType!;

        if (!counterparts.TryGetValue(declaring.Name, out var rewrittenType))
        {
            refused.Add(new Skipped(target.Display, SkipReason.NoCounterpart, declaring.Name));
            return null;
        }

        var twin = Twin(rewrittenType, method);

        if (twin is null)
        {
            // The method is gone, or its parameters changed. Both are real
            // findings about a rewrite and neither is a behaviour difference,
            // so neither is reported as one.
            refused.Add(new Skipped(target.Display, SkipReason.SignatureChanged, rewrittenType.Name));
            return null;
        }

        object? here = null;
        object? there = null;

        if (!method.IsStatic)
        {
            here = Construct(declaring);
            there = Construct(rewrittenType);

            if (here is null || there is null)
            {
                refused.Add(new Skipped(target.Display, SkipReason.NotConstructible,
                    here is null ? declaring.Name : rewrittenType.Name));
                return null;
            }
        }

        var divergences = new List<Divergence>();
        var cases = 0;

        foreach (var arguments in Values.Cases(parameters, CasesPerMethod,
            type => mentioned.TryGetValue(type, out var values) ? values : []))
        {
            // Observed rather than invoked: the observer calls twice and keeps
            // the result only if the two runs agree, so a method reading a
            // clock is dropped instead of being reported as a change.
            var (first, why) = observer.Observe(method, here, arguments);

            if (first is null)
            {
                if (why is not null) refused.Add(new Skipped(target.Display, why.Value));
                continue;
            }

            var (second, alsoWhy) = observer.Observe(twin, there, arguments);

            cases++;

            // The original produced an observation and the rewrite did not.
            // That is a difference rather than a refusal, and it is one of the
            // ones worth catching: a rewrite that starts reading a clock, or
            // stops returning in time, has changed what the code does even
            // though it still returns something.
            if (second is null)
            {
                Record(divergences, arguments, Describe(first), Failure(alsoWhy));
                continue;
            }

            if (Same(first, second)) continue;

            Record(divergences, arguments, Describe(first), Describe(second));
        }

        if (cases == 0)
        {
            // Every case was dropped by one side or the other, so the method
            // was named but never actually compared. Counting it as matched
            // would inflate the only number anybody reads.
            refused.Add(new Skipped(target.Display, SkipReason.NothingLeftToCompare));
            return null;
        }

        var note = method.ReturnType.Name == twin.ReturnType.Name
            ? null
            : $"The return type changed from {method.ReturnType.Name} to {twin.ReturnType.Name}.";

        return new Compared(
            declaring.Name, method.Name, Signature(method), cases, divergences, note);
    }

    /// <summary>
    /// Whether a value of this type can be handed to both versions.
    ///
    /// Framework types only. A type the file declares is recompiled into the
    /// other assembly as a different type with the same name, and passing one
    /// across raises an argument exception that would read like a crash in the
    /// code being examined.
    /// </summary>
    private static bool Portable(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;

        return actual.Namespace is { } space
            && (space == "System" || space.StartsWith("System.", StringComparison.Ordinal))
            && Values.For(type).Count > 0;
    }

    /// <summary>
    /// The rewritten file's types, by their short name.
    ///
    /// Short rather than full, because a rewrite that moves a class into
    /// another namespace has not made it a different class, and matching on the
    /// full name would report every namespace change as a deletion. A name that
    /// appears twice is left out: guessing which one is meant would be
    /// comparing against whichever happened to be found first.
    /// </summary>
    private static IReadOnlyDictionary<string, Type> Counterparts(Assembly assembly)
    {
        Type?[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types;
        }

        return types
            .OfType<Type>()
            .Where(t => t is { IsPublic: true, IsInterface: false })
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The same method on the rewritten type, or none.
    ///
    /// Matched on the name and the parameter types by name. A rewrite that
    /// changed either has changed the method's contract, which is a finding of
    /// its own rather than something to compare through.
    /// </summary>
    private static MethodInfo? Twin(Type rewritten, MethodInfo method)
    {
        var wanted = method.GetParameters().Select(p => p.ParameterType.Name).ToList();

        return rewritten
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                      | BindingFlags.DeclaredOnly)
            .FirstOrDefault(candidate =>
                candidate.Name == method.Name
                && candidate.IsStatic == method.IsStatic
                && candidate.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(wanted));
    }

    private static object? Construct(Type type)
    {
        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the two calls did the same thing.
    ///
    /// Not the observer's own fingerprint, and the difference is deliberate.
    /// There, two runs of the same method must agree exactly, because anything
    /// else is a clock. Here the two runs are of two different methods, and a
    /// number's C# suffix is a fact about writing it down rather than about the
    /// value: a rewrite widening `int` to `long` returns 1 where the original
    /// returned 1, and reporting that as a behaviour change would put a false
    /// positive on one of the most common things a migration does.
    ///
    /// The suffix is all that is set aside. A different number still differs, a
    /// string keeps its quotes so it can never be mistaken for a number, and
    /// the change of type is reported as a note beside the method.
    /// </summary>
    private static bool Same(Observation before, Observation after) =>
        before.Shape == after.Shape
        && before.ExceptionType == after.ExceptionType
        && (before.Rendered == after.Rendered
            || (before.Shape == ResultShape.Literal
                && Unsuffixed(before.Rendered) == Unsuffixed(after.Rendered)));

    private static readonly string[] Suffixes = ["UL", "ul", "L", "l", "u", "U", "m", "M", "f", "F", "d", "D"];

    private static string Unsuffixed(string rendered)
    {
        // Only where the rest is a number. Stripping a trailing d from a bare
        // word would turn two different results into one.
        foreach (var suffix in Suffixes)
        {
            if (!rendered.EndsWith(suffix, StringComparison.Ordinal)) continue;

            var body = rendered[..^suffix.Length];
            if (body.Length > 0 && Numeric(body)) return body;
        }

        return rendered;
    }

    private static bool Numeric(string text) =>
        text.All(c => char.IsAsciiDigit(c) || c is '-' or '+' or '.');

    private static void Record(
        List<Divergence> divergences, object?[] arguments, string before, string after)
    {
        if (divergences.Count >= ShownPerMethod) return;

        divergences.Add(new Divergence(
            string.Join(", ", arguments.Select(Values.Literal)), before, after));
    }

    /// <summary>
    /// What it means when one side produced nothing.
    ///
    /// Said in the same register as a result, because it sits in the same
    /// column next to one: the reader is comparing two outcomes, and "no longer
    /// returns the same thing twice" is an outcome.
    /// </summary>
    private static string Failure(SkipReason? reason) => reason switch
    {
        SkipReason.NotDeterministic => "no longer returns the same thing twice",
        SkipReason.TooSlow => "no longer returns within the time allowed",
        SkipReason.ResultNotComparable => "returns something that can no longer be compared",
        SkipReason.DependencyMissing => "needs an assembly that is not on this machine",
        _ => "produced nothing that could be recorded",
    };

    /// <summary>What came back, said the way a reader would say it.</summary>
    private static string Describe(Observation observation) => observation.Shape switch
    {
        ResultShape.Threw => $"threw {Short(observation.ExceptionType)}",
        _ => observation.Rendered,
    };

    private static string Short(string? typeName) =>
        typeName?.Split('.').LastOrDefault() ?? "an exception";

    private static string Signature(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
}
