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
/// </summary>
internal static class CompareBehaviour
{
    /// <summary>equivalence &lt;before.cs&gt; &lt;after.cs&gt;</summary>
    public static void Run(string beforePath, string afterPath)
    {
        foreach (var path in new[] { beforePath, afterPath })
        {
            if (File.Exists(path)) continue;

            Console.Error.WriteLine($"No such file: {Path.GetFullPath(path)}");
            return;
        }

        var report = new Equivalence().Compare(
            File.ReadAllText(beforePath), File.ReadAllText(afterPath));

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
