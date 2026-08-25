namespace LegacyLens.Analysis;

/// <summary>Where a type the catalogue never mentions stands with the target framework.</summary>
public enum Standing
{
    /// <summary>A type of that name lives in the successor's own namespaces. A lead.</summary>
    InSuccessor,

    /// <summary>A type of that name lives elsewhere in the framework. A trap, not an answer.</summary>
    Elsewhere,

    /// <summary>No type of that name exists anywhere. Gone, and somebody has to decide.</summary>
    Gone,
}

/// <summary>One type nobody catalogued, and what the framework says about it.</summary>
public record UnlistedType(ApiUse Use, Standing Standing, string? Where);

/// <summary>What a whole unknown column turned out to be.</summary>
public record UnlistedReading(IReadOnlyList<UnlistedType> Types, bool Applicable = true)
{
    public IReadOnlyList<UnlistedType> Of(Standing standing) =>
        Types.Where(t => t.Standing == standing).ToList();

    public int Uses(Standing standing) => Of(standing).Sum(t => t.Use.Uses);

    /// <summary>
    /// What is actually left to decide.
    ///
    /// A lead is not work: the name is waiting in the successor and whoever
    /// checks it will be done in a minute. A trap is, because the reader has to
    /// find out what the old type really did.
    /// </summary>
    public int Left => Of(Standing.Gone).Count + Of(Standing.Elsewhere).Count;
}

/// <summary>
/// Asks the target framework about the types nobody wrote down.
///
/// The catalogue of successors is written by hand, and stays that way: what
/// replaces what is a judgement with a note attached, and a machine that
/// invents one is the failure this project exists to avoid. But the column
/// beside it, the types the catalogue says nothing about, is not a judgement at
/// all. It is a question of fact, and the framework being migrated to is right
/// here, loaded into this process.
///
/// Measured on Orchard, against the package holding 73 of its 89 projects: of
/// the 150 types the catalogue never mentions, 118 exist nowhere in modern .NET
/// at all, and 15 have a same-named type somewhere else. That second group is
/// worse than no answer if it is reported as one: `System.Web.HttpContext` and
/// `Microsoft.AspNetCore.Http.HttpContext` share a word and nothing else, and
/// that particular pair is the hardest part of the migration rather than a
/// rename.
///
/// So three answers, and only one of them is a lead. Nothing here is written
/// into the catalogue and nothing here claims to be a correspondence: the
/// generated part stays visibly apart from the written part, or the distinction
/// that makes the catalogue worth trusting dissolves into it.
/// </summary>
public class Unlisted
{
    /// <summary>
    /// Reads a coverage's unknown column against the framework.
    ///
    /// <paramref name="successorPackage"/> is the candidate this coverage is
    /// for, used as the namespace root to look under. Empty for a candidate
    /// whose answer is deletion: there is no successor to look inside.
    /// </summary>
    public UnlistedReading Read(IEnumerable<ApiUse> unknown, string successorPackage)
    {
        // Only worth asking where the successor is part of the framework.
        // log4net's answer is Serilog, which is a package: every type of every
        // predecessor comes back absent from the framework, which is literally
        // true and tells nobody anything. Saying so beats printing 22 types
        // under "the framework does not have at all" and letting a reader
        // conclude they are gone.
        // A named successor the runtime does not carry, and only that. An empty
        // one means the answer is deletion, and there the question still stands:
        // a name that survives somewhere is a trap and a name that does not is
        // gone, whether or not anything succeeds the package.
        if (successorPackage.Length > 0 && !FrameworkTypes.Carries(successorPackage))
            return new UnlistedReading([], Applicable: false);

        var read = new List<UnlistedType>();

        foreach (var use in unknown)
        {
            var everywhere = FrameworkTypes.ByName.TryGetValue(use.Name, out var found)
                ? found
                : [];

            // A fourth answer used to live here, for a name the base library
            // still supplies. It was always right and it is now unreachable:
            // the usage surface stopped attributing those to the package at
            // all, which is a better place to deal with them than an
            // explanation further down. On Orchard it was 69 types over 502
            // calls, and they never belonged in the estimate to begin with.
            var inside = successorPackage.Length > 0
                ? FrameworkTypes.Under(use.Name, successorPackage)
                : [];

            if (inside.Count > 0)
            {
                read.Add(new UnlistedType(use, Standing.InSuccessor, inside[0]));
                continue;
            }

            read.Add(everywhere.Count > 0
                ? new UnlistedType(use, Standing.Elsewhere, everywhere[0])
                : new UnlistedType(use, Standing.Gone, null));
        }

        return new UnlistedReading(read);
    }
}
