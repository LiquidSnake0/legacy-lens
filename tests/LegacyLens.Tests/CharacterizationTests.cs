using System.Reflection;
using LegacyLens.Characterization;

namespace LegacyLens.Tests;

/// <summary>
/// A tool that writes tests has to be held to what it promises about them:
/// that nothing is offered which did not compile and pass, that a method whose
/// answer changes between two identical calls is refused rather than pinned,
/// and that every refusal is counted rather than dropped.
///
/// The subject is the sample type at the bottom of this file, characterized
/// through reflection exactly as a real assembly would be.
/// </summary>
public class CharacterizationTests
{
    private static readonly Assembly Self = typeof(CharacterizationTests).Assembly;

    /// <summary>
    /// Narrowed to the sample type. Pointing it at the whole test assembly
    /// would characterize the test suite itself, which is slow, and would make
    /// these assertions depend on every other file in the project.
    /// </summary>
    private static CharacterizationRun Run() =>
        new Characterizer { CasesPerMethod = 3, Types = t => t == typeof(Sample) }.Run(Self);

    private static IEnumerable<Skipped> SkippedFor(CharacterizationRun run, string name) =>
        run.Skipped.Where(s => s.Member.Contains(name, StringComparison.Ordinal));

    [Fact]
    public void A_pure_method_is_characterized()
    {
        var run = Run();
        var file = run.Files.FirstOrDefault(f => f.FileName.Contains("Sample"));

        Assert.NotNull(file);
        Assert.Contains("Doubled", file.Source);
        Assert.True(file.Cases > 0);
    }

    [Fact]
    public void Everything_offered_compiled_and_passed()
    {
        // The promise the whole approach rests on. A file reaches the caller
        // only after Verifier compiled it and ran every method in it, so
        // re-verifying here asserts the pipeline rather than the sample.
        var run = Run();

        foreach (var file in run.Files)
        {
            var suite = new GeneratedSuite(file.Source, []);
            var verification = new Verifier().Verify(suite, Self);

            Assert.True(verification.Compiled,
                $"{file.FileName}: {string.Join("; ", verification.CompilerErrors)}");
            Assert.Empty(verification.Failed);
        }
    }

    [Fact]
    public void A_method_that_answers_differently_each_time_is_refused()
    {
        // The failure mode that would poison a generated suite: a test that
        // passes when written and fails the next morning.
        var run = Run();

        Assert.Contains(SkippedFor(run, nameof(Sample.Now)),
            s => s.Reason == SkipReason.NotDeterministic);

        Assert.DoesNotContain(run.Files, f => f.Source.Contains(nameof(Sample.Now)));
    }

    [Fact]
    public void A_thrown_exception_is_recorded_as_the_behaviour()
    {
        // Legacy code throws on inputs nobody expected, and that is behaviour
        // somebody may be relying on without knowing it.
        var run = Run();
        var file = run.Files.First(f => f.FileName.Contains("Sample"));

        Assert.Contains("Assert.Throws<global::System.ArgumentException>", file.Source);
    }

    [Fact]
    public void A_void_method_is_refused_with_a_reason()
    {
        var run = Run();

        Assert.Contains(SkippedFor(run, nameof(Sample.Ignore)),
            s => s.Reason == SkipReason.NothingToObserve);
    }

    [Fact]
    public void A_parameter_nobody_modelled_is_refused_rather_than_guessed()
    {
        var run = Run();

        Assert.Contains(SkippedFor(run, nameof(Sample.Takes)),
            s => s.Reason == SkipReason.ParameterTypeNotSupported);
    }

    [Fact]
    public void The_generated_file_says_what_it_is()
    {
        // Somebody will meet this file during a failure, months later, and the
        // first thing they need is that it records old behaviour rather than
        // intended behaviour.
        var run = Run();
        var file = run.Files.First();

        Assert.Contains("auto-generated", file.Source);
        Assert.Contains("recorded the bug", file.Source);
    }

    [Fact]
    public void Refusals_are_counted_by_reason()
    {
        var run = Run();

        Assert.NotEmpty(run.Refusals);
        Assert.Equal(run.Skipped.Count, run.Refusals.Sum(r => r.Count));
    }

    [Fact]
    public void A_missing_assembly_is_not_mistaken_for_behaviour()
    {
        // Measured on Orchard's own dependencies: MSBuild.Community.Tasks loads
        // and then throws FileNotFoundException for Microsoft.Build.Framework
        // the moment a return type is read. Recorded as behaviour, that becomes
        // a test asserting the code throws, which then fails on every machine
        // where the assembly is actually present.
        Assert.True(TargetFinder.IsMissingDependency(new FileNotFoundException()));
        Assert.True(TargetFinder.IsMissingDependency(new TypeLoadException()));
        Assert.False(TargetFinder.IsMissingDependency(new ArgumentException()));
        Assert.False(TargetFinder.IsMissingDependency(new InvalidOperationException()));
    }

    [Fact]
    public void Literals_survive_a_round_trip_through_the_compiler()
    {
        // A quote inside a string, escaped by hand, is the classic way a
        // generator emits something that does not compile.
        Assert.Equal("\"he said \\\"no\\\"\"", Values.Literal("he said \"no\""));
        Assert.Equal("null", Values.Literal(null));
        Assert.Equal("(short)1", Values.Literal((short)1));
        Assert.Equal("1.5d", Values.Literal(1.5d));
    }

    [Fact]
    public void Cases_walk_the_values_in_step_rather_than_multiplying_them()
    {
        // Six parameters with five values each is 15,625 calls into code
        // nobody has read.
        var cases = Values.Cases([typeof(int), typeof(string), typeof(bool)], limit: 10);

        Assert.Equal(6, cases.Count);
        Assert.All(cases, arguments => Assert.Equal(3, arguments.Length));
    }
}

/// <summary>
/// The subject under characterization. Every member here exists to exercise one
/// decision the tool makes, and it is public because reflection over a compiled
/// assembly is exactly how the tool sees real code.
/// </summary>
public class Sample
{
    public int Doubled(int value) => value * 2;

    public string Shout(string text) =>
        text.Length == 0 ? throw new ArgumentException("empty") : text.ToUpperInvariant();

    /// <summary>Different on every call, and must never be pinned.</summary>
    public long Now() => DateTime.UtcNow.Ticks;

    /// <summary>Nothing to observe but a side effect.</summary>
    public void Ignore(int value) { }

    /// <summary>A parameter type the tool has no values for.</summary>
    public int Takes(Stream stream) => 0;
}
