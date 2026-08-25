using LegacyLens.Characterization;

namespace LegacyLens.Api;

/// <summary>
/// The command behind `equivalence`.
///
/// Kept beside <see cref="Characterize"/> for the same reason: these are the
/// two capabilities that run code somebody else wrote, and they should be
/// findable rather than folded into a startup file.
///
/// The command needs no setting turned on. Someone typing it into their own
/// terminal, against two paths they chose, has already made the decision that
/// the server cannot make on their behalf.
///
/// It is also the far end of <see cref="Detached"/>. A server that compares
/// behaviour runs this exact command in a process of its own and reads what it
/// printed, which is why <c>--json</c> exists: the same run, written for a
/// program rather than for a person. One implementation, two readers. A second
/// entry point that recomputed any of this would be a second answer waiting to
/// disagree with the first.
/// </summary>
internal static class CompareBehaviour
{
    /// <summary>Print the report as data instead of as a page.</summary>
    public const string Machine = "--json";

    /// <summary>equivalence [--json] &lt;before.cs&gt; &lt;after.cs&gt;</summary>
    public static int Run(string[] arguments)
    {
        var machine = arguments.Contains(Machine, StringComparer.Ordinal);
        var paths = arguments.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        if (paths.Length < 2)
        {
            Console.Error.WriteLine("Usage: equivalence [--json] <before.cs> <after.cs>");
            return 2;
        }

        var (beforePath, afterPath) = (paths[0], paths[1]);

        var missing = new[] { beforePath, afterPath }.FirstOrDefault(path => !File.Exists(path));

        if (missing is not null)
        {
            var why = $"Nothing was checked: no such file: {Path.GetFullPath(missing)}.";

            // Even a usage mistake has to come back as a report when a program
            // is reading. A caller that gets a bare message on one path and a
            // report on the other has to parse both to find out which it got.
            if (machine) Console.Out.Write(Wire.Write(Nothing(why)));
            else Console.Error.WriteLine($"No such file: {Path.GetFullPath(missing)}");

            return 2;
        }

        var report = new Equivalence().Compare(
            File.ReadAllText(beforePath), File.ReadAllText(afterPath));

        if (machine)
        {
            // Nothing else on this stream. The parent reads all of it and hands
            // it to a parser, and one stray line of courtesy would be the whole
            // difference between a report and an unreadable one.
            Console.Out.Write(Wire.Write(report));
            return 0;
        }

        Print(report, beforePath, afterPath);
        return 0;
    }

    private static EquivalenceReport Nothing(string why) =>
        new(false, [], [], [], [], 0, why);

    private static void Print(EquivalenceReport report, string beforePath, string afterPath)
    {
        Console.WriteLine();
        Console.WriteLine($"  {Path.GetFileName(beforePath)} -> {Path.GetFileName(afterPath)}");
        Console.WriteLine();

        if (!report.Ran)
        {
            Console.WriteLine($"  {report.Claim}");
            Console.WriteLine();

            foreach (var error in report.BeforeErrors.Concat(report.AfterErrors).Take(8))
                Console.WriteLine($"    {error}");

            Console.WriteLine();
            return;
        }

        foreach (var method in report.Methods)
        {
            // The mark is a word rather than a colour: this prints into logs,
            // pipes and terminals that have no colour to give it.
            var mark = method.Matched ? "same" : "MOVED";

            Console.WriteLine($"  {mark,5}  {method.Type}.{method.Signature}  ({method.Cases} call(s))");

            if (method.Note is not null) Console.WriteLine($"         {method.Note}");

            foreach (var divergence in method.Divergences)
            {
                Console.WriteLine($"         ({divergence.Arguments})");
                Console.WriteLine($"           was  {divergence.Before}");
                Console.WriteLine($"           now  {divergence.After}");
            }
        }

        if (report.Methods.Count > 0) Console.WriteLine();

        // Printed after the results and never before them. This is the half
        // that decides what the rest means, and a reader who stops at the first
        // screen should have seen the comparisons, not the excuses.
        if (report.Refusals.Count > 0)
        {
            Console.WriteLine("  Passed over");
            foreach (var (reason, count) in report.Refusals)
                Console.WriteLine($"  {count,6}  {Reasons.Explain(reason)}");

            Console.WriteLine();
        }

        Console.WriteLine($"  {report.Claim}");
        Console.WriteLine();
    }
}
