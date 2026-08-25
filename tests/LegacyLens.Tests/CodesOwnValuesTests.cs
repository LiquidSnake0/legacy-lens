using System.Reflection;
using LegacyLens.Characterization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LegacyLens.Tests;

/// <summary>
/// Whether the boundaries written into the code are worth spending cases on.
///
/// The question is not whether more cases catch more, which is trivially true
/// and not worth a milestone. A characterization run writes a file somebody has
/// to read and commit, so a case costs a reader. What has to be shown is that
/// these particular cases catch changes the invented ones cannot, and how many
/// of them it takes.
///
/// Measured by mutation, because it is the only way to ask a generated suite
/// whether it would have noticed. Each mutant is a small edit of the kind that
/// looks like tidying, compiled separately, with the suite generated from the
/// original run against it.
/// </summary>
public class CodesOwnValuesTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(), "legacylens-values-" + Guid.NewGuid().ToString("N"));

    public CodesOwnValuesTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A class of the kind this tool exists for: a few boundaries somebody
    /// chose, written down in the code and nowhere else.
    /// </summary>
    private const string Subject = """
        public class Pricing
        {
            public const int Seniority = 3;

            public string Tier(int years)
            {
                if (years >= Seniority) return "senior";
                return "junior";
            }

            public int Fee(int amount)
            {
                if (amount > 100) return amount / 10;
                return 0;
            }

            public int Role(string name)
            {
                if (name == "admin") return 7;
                return 0;
            }

            public int Tax(int amount)
            {
                return amount * 20 / 100;
            }
        }
        """;

    /// <summary>
    /// The edits. Each one looks like tidying and each one changes behaviour.
    ///
    /// The last is the control: an overflow at the extreme, which the invented
    /// values already reach. A measurement where nothing is caught without the
    /// code's own values would be measuring a broken harness.
    /// </summary>
    private static readonly (string Name, string From, string To)[] Mutations =
    [
        ("boundary >= becomes >", "years >= Seniority", "years > Seniority"),
        ("boundary > becomes >=", "amount > 100", "amount >= 100"),
        ("the string it compares", "\"admin\"", "\"admins\""),
        ("control: overflow at the extreme", "amount * 20 / 100", "amount / 5"),
    ];

    private Assembly Build(string source, string name)
    {
        var references = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var path = Path.Combine(_work, name + ".dll");
        var emitted = compilation.Emit(path);

        Assert.True(emitted.Success, string.Join("; ",
            emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        return Assembly.LoadFrom(path);
    }

    private static CharacterizationRun Characterize(Assembly subject, bool own, int cases) =>
        new Characterizer
        {
            CasesPerMethod = cases,
            UsesTheCodesOwnValues = own,
            Types = type => type.Name == "Pricing",
        }.Run(subject);

    /// <summary>Whether the suite, run against the mutant, notices.</summary>
    private static bool Notices(CharacterizationRun run, Assembly mutant)
    {
        var noticed = false;

        foreach (var file in run.Files)
        {
            var checking = new Verifier().Verify(new GeneratedSuite(file.Source, []), mutant);

            Assert.True(checking.Compiled, "the suite has to compile against the mutant: "
                                         + string.Join("; ", checking.CompilerErrors));

            // A harness that runs nothing reports every mutant as survived,
            // which reads exactly like a suite that is no good. This is the
            // assertion that tells the two apart.
            Assert.True(checking.Passed.Count + checking.Failed.Count > 0,
                "the mutant has to actually be run against");

            noticed |= checking.Failed.Count > 0;
        }

        return noticed;
    }

    private (int Caught, int Total, int Cases) Measure(bool own, int budget, string label)
    {
        var original = Build(Subject, $"Subject_{label}");
        var run = Characterize(original, own, budget);

        var caught = 0;
        var lines = new List<string>();

        for (var i = 0; i < Mutations.Length; i++)
        {
            var (name, from, to) = Mutations[i];

            Assert.Contains(from, Subject);

            var mutant = Build(Subject.Replace(from, to), $"Mutant_{label}_{i}");
            var noticed = Notices(run, mutant);

            if (noticed) caught++;
            lines.Add($"    {(noticed ? "caught " : "MISSED ")} {name}");
        }

        File.AppendAllText(Path.Combine(Path.GetTempPath(), "legacylens-m16.txt"),
            $"  own={own} budget={budget}: {caught}/{Mutations.Length} caught, "
          + $"{run.Tests} test(s) written\n" + string.Join("\n", lines) + "\n");

        return (caught, Mutations.Length, run.Tests);
    }

    /// <summary>
    /// The measurement this milestone exists to produce.
    ///
    /// Two claims, and the second is the one that decides the budget. The
    /// code's own values catch changes the invented ones never reach, and at
    /// the default budget they cost nothing at all: the same number of tests
    /// are written either way, because the cases were already being spent, just
    /// on values that told nobody anything.
    /// </summary>
    [Fact]
    public void The_codes_own_values_catch_what_invented_ones_cannot()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "legacylens-m16.txt"), "");

        var without = Measure(own: false, budget: 4, "off4");
        var with = Measure(own: true, budget: 4, "on4");

        Assert.True(with.Caught > without.Caught,
            $"the code's own values have to catch more: {with.Caught} against {without.Caught}");

        Assert.Equal(without.Cases, with.Cases);
    }

    [Fact]
    public void Reading_the_code_is_what_a_run_does_unless_it_is_told_otherwise()
    {
        // Every other measurement here sets the switch, which means none of
        // them says anything about where it sits when nobody touches it. Left
        // untested, the whole of this milestone could be turned off by a
        // one-word edit with the suite still green.
        var original = Build(Subject, "Subject_default");

        var run = new Characterizer
        {
            CasesPerMethod = 4,
            Types = type => type.Name == "Pricing",
        }.Run(original);

        var mutant = Build(Subject.Replace("years >= Seniority", "years > Seniority"), "Mutant_default");

        Assert.True(Notices(run, mutant),
            "a run nobody configured still tries the boundary the code names");
    }

    [Fact]
    public void Inventing_harder_does_not_replace_reading_the_code()
    {
        // The plateau. What this tool invents is six values long, so past the
        // budget that reaches all six, spending more of a reader's attention
        // buys nothing: the boundary the code turns on is not in the list and
        // never will be.
        var counts = new[] { 6, 10, 20 }
            .Select(budget => Measure(own: false, budget, $"plateau{budget}"))
            .ToList();

        Assert.All(counts, measured => Assert.Equal(counts[0].Caught, measured.Caught));
        Assert.All(counts, measured => Assert.Equal(counts[0].Cases, measured.Cases));

        // And what it does catch is the one an extreme reaches, which is the
        // control: a harness where invented values caught nothing would be
        // measuring itself.
        Assert.Equal(1, counts[0].Caught);
    }

    [Fact]
    public void Every_boundary_is_reached_by_spending_more_of_a_readers_attention()
    {
        // Written down rather than made the default. Going from four cases to
        // ten catches the last two mutants and more than doubles the file
        // somebody has to read and commit, which is a trade for whoever is
        // reading it rather than one this tool should make on their behalf.
        var generous = Measure(own: true, budget: 10, "generous");
        var ordinary = Measure(own: true, budget: 4, "ordinary");

        Assert.Equal(Mutations.Length, generous.Caught);
        Assert.True(generous.Cases > ordinary.Cases * 2);
    }
}
