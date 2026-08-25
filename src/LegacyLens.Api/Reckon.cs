using LegacyLens.Analysis;

namespace LegacyLens.Api;

/// <summary>
/// The command behind `hindsight`.
///
/// Two directories, the same product before and after somebody migrated it,
/// and the question nobody in this field can normally ask: was the tool right?
///
/// It reads syntax on both sides and compiles nothing, so it runs on a legacy
/// tree that does not build, which is every legacy tree. A command rather than
/// a route for the same reason as the rest: it is a measurement somebody runs
/// deliberately against two trees they chose, and its output is meant to be
/// read or piped, not polled.
/// </summary>
internal static class Reckon
{
    /// <summary>hindsight &lt;before&gt; &lt;after&gt;</summary>
    public static int Run(string[] arguments)
    {
        var paths = arguments.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        if (paths.Length < 2)
        {
            Console.Error.WriteLine("Usage: hindsight <before> <after>");
            return 2;
        }

        IReadOnlyList<Reckoning> reckonings;

        try
        {
            reckonings = new Hindsight().Compare(paths[0], paths[1]);
        }
        catch (DirectoryNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        if (reckonings.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Nothing catalogued was used by the older tree, so there is "
                            + "nothing to have been right or wrong about.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  {Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))}"
                        + $" -> {Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[1]))}");
        Console.WriteLine();

        foreach (var reckoning in reckonings)
        {
            Console.WriteLine($"  {reckoning.Package}");
            Console.WriteLine(
                $"    before   {reckoning.UsesBefore} use(s) of {reckoning.TypesBefore} "
              + $"type(s), across {reckoning.FilesBefore} file(s)");

            Console.WriteLine(reckoning.Proposed is null
                ? "    proposed nothing: the catalogue has no candidate for it"
                : $"    proposed {reckoning.Proposed}, covering {reckoning.Coverage}% of the calls");

            Console.WriteLine($"    became   {reckoning.Claim}");

            // The mark is a word rather than a colour: this prints into logs
            // and pipes that have no colour to give it.
            Console.WriteLine(reckoning.Agreed switch
            {
                true => "    the number pointed the way they went",
                false => "    THE NUMBER POINTED THE OTHER WAY",
                _ => "    nothing was claimed, so nothing was right or wrong",
            });

            Console.WriteLine();
        }

        var agreement = Hindsight.Agreement(reckonings);

        if (agreement is var (agreed, judged) && agreement is not null)
        {
            Console.WriteLine(
                $"  {agreed} of {judged} claim(s) pointed the way the team went. Above "
              + $"{Hindsight.Substitutable}% covered is read as a substitution they would make, "
              + "below it as a rewrite they would not.");
        }
        else
        {
            Console.WriteLine("  The catalogue claimed nothing about any of these, so there is "
                            + "no agreement to report.");
        }

        Console.WriteLine();
        return 0;
    }
}
