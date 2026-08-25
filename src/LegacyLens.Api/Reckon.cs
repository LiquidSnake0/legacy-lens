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

            Console.WriteLine(reckoning.Strands switch
            {
                true => "    expected gone: it has no life on the target, whatever anyone prefers",
                false => "    expected to stay: it runs on the target, so moving is a choice",
                _ => "    expected nothing: nobody recorded whether it can stay",
            });

            // Printed after the prediction and never as it: this estimates how
            // much of a move is a substitution rather than a rewrite, which is
            // a cost. It was marked as a prediction for one milestone and got
            // four of ten, because it was answering a different question.
            Console.WriteLine(reckoning.Proposed is null
                ? "    no candidate recorded, so nothing about the cost of moving"
                : $"    {reckoning.Coverage}% of the calls have a recorded counterpart in "
                + $"{reckoning.Proposed}");

            Console.WriteLine($"    became   {reckoning.Claim}");

            // The mark is a word rather than a colour: this prints into logs
            // and pipes that have no colour to give it.
            Console.WriteLine(reckoning.Agreed switch
            {
                true => "    the prediction held",
                false => "    THE PREDICTION DID NOT HOLD",
                _ => "    nothing was claimed, so nothing was right or wrong",
            });

            Console.WriteLine();
        }

        var marking = Hindsight.Mark(reckonings);

        if (!marking.Anything)
        {
            Console.WriteLine("  Nobody has recorded whether any of these can stay, so there is "
                            + "nothing to have been right or wrong about.");
        }
        else
        {
            // Two rates and never one. Across four real migrations the forced
            // half held fifteen times out of fifteen and the discretionary half
            // six out of twelve; a single blended number would have hidden the
            // only thing worth knowing.
            Console.WriteLine(
                $"  {marking.ForcedHeld} of {marking.Forced}: what the target leaves no choice "
              + "about. A package with no life there goes, whatever anyone would have preferred.");

            Console.WriteLine(
                $"  {marking.ChosenHeld} of {marking.Chosen}: what a team was free to decide "
              + "either way. This is not a prediction and should not be read as one. A port "
              + "keeps what still runs; a rewrite keeps nothing.");
        }

        Console.WriteLine();
        return 0;
    }
}
