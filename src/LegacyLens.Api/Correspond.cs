using LegacyLens.Analysis;

namespace LegacyLens.Api;

/// <summary>
/// The command behind `correspondences`.
///
/// One level below `hindsight`. That one asks whether a dependency moved at
/// all; this one asks whether the type-by-type correspondences the catalogue
/// records are the ones a team actually wrote, and what they wrote that nobody
/// has written down.
/// </summary>
internal static class Correspond
{
    /// <summary>correspondences &lt;before&gt; &lt;after&gt;</summary>
    public static int Run(string[] arguments)
    {
        var paths = arguments.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        if (paths.Length < 2)
        {
            Console.Error.WriteLine("Usage: correspondences <before> <after>");
            return 2;
        }

        IReadOnlyList<Correspondence> found;

        try
        {
            found = new Correspondences().Compare(paths[0], paths[1]);
        }
        catch (DirectoryNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        // The same list as data, ready to merge into the catalogue. Printed
        // rather than written: this tool proposes and a person applies, and a
        // program editing its own catalogue is the one place that rule would be
        // easiest to lose.
        if (arguments.Contains("--catalogue", StringComparer.Ordinal))
        {
            foreach (var package in found.Where(c => c.Candidate).GroupBy(c => c.Package, StringComparer.Ordinal))
            {
                Console.Out.WriteLine($"{package.Key}:");

                foreach (var one in package.OrderByDescending(c => c.Uses))
                    Console.Out.WriteLine($"  {one.Type}\t{one.InSuccessor}\t{one.Uses}");
            }

            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  {Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))}"
                        + $" -> {Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[1]))}");
        Console.WriteLine();

        var (confirmed, recorded) = Correspondences.Mark(found);

        if (recorded == 0)
        {
            Console.WriteLine("  The catalogue records no correspondence for anything this code "
                            + "used, so there is nothing to have been right about.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"  {confirmed} of {recorded} recorded correspondence(s) turned up in "
                        + "the finished code.");
        Console.WriteLine();

        // Never called wrong. A counterpart that does not appear may simply be
        // one the team never needed, and this cannot tell the two apart.
        var unseen = found.Where(c => c.Recorded is not null && !c.CounterpartSeen).Take(10).ToList();

        if (unseen.Count > 0)
        {
            Console.WriteLine("  Recorded and not seen, which is evidence rather than a verdict:");
            foreach (var one in unseen)
                Console.WriteLine($"    {one.Type} -> {one.Recorded}  ({one.Uses} use(s))");

            Console.WriteLine();
        }

        var candidates = found.Where(c => c.Candidate).ToList();

        if (candidates.Count > 0)
        {
            Console.WriteLine($"  {candidates.Count} name(s) the old code used, the catalogue does "
                            + "not mention, and the successor has under the same name:");

            foreach (var one in candidates.Take(20))
                Console.WriteLine($"    {one.Type}  ({one.Uses} use(s), {one.Package})");

            if (candidates.Count > 20)
                Console.WriteLine($"    and {candidates.Count - 20} more.");

            Console.WriteLine();
            Console.WriteLine("  Candidates for somebody to sign, not entries. A name surviving "
                            + "into an unrelated place is a trap, which is why only the "
                            + "successor was looked in.");
            Console.WriteLine();
        }

        return 0;
    }
}
