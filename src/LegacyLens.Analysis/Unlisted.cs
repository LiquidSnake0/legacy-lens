namespace LegacyLens.Analysis;

/// <summary>Where a type the catalogue never mentions stands with the target framework.</summary>
public enum Standing
{
    /// <summary>Still provided under <c>System.*</c>. The code keeps it as it is.</summary>
    Unchanged,

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
public record UnlistedReading(IReadOnlyList<UnlistedType> Types)
{
    public IReadOnlyList<UnlistedType> Of(Standing standing) =>
        Types.Where(t => t.Standing == standing).ToList();

    public int Uses(Standing standing) => Of(standing).Sum(t => t.Use.Uses);

    /// <summary>
    /// What is actually left to decide, once the noise is out.
    ///
    /// The number this exists to produce. A column of "the catalogue says
    /// nothing about these" is honest and unusable: it is read as work, and
    /// most of it is not.
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
/// 219 types the catalogue never mentions, over 1,779 calls, **69 are still
/// provided under System.\* unchanged**. They were counted as work for years.
/// Another 15 have a same-named type somewhere else in modern .NET, which is
/// worse than no answer if it is reported as one: `System.Web.HttpContext` and
/// `Microsoft.AspNetCore.Http.HttpContext` share a word and nothing else, and
/// that particular pair is the hardest part of the migration rather than a
/// rename.
///
/// So four answers, and only one of them is a lead. Nothing here is written
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
        var read = new List<UnlistedType>();

        foreach (var use in unknown)
        {
            var everywhere = FrameworkTypes.ByName.TryGetValue(use.Name, out var found)
                ? found
                : [];

            // Still in the base library, so the code keeps it whatever brought
            // it into this file.
            //
            // System.Web is not excluded, and an earlier version of this did
            // exclude it on the assumption that the whole family went away.
            // Modern .NET keeps exactly two of them, System.Web.HttpUtility and
            // System.Web.IHtmlString, and Orchard uses the second more than a
            // hundred times: excluding them reported two survivors as losses.
            // The question here is only ever what the target framework has, and
            // this set was read from the target framework.
            var bcl = everywhere.FirstOrDefault(
                full => full.StartsWith("System.", StringComparison.Ordinal));

            if (bcl is not null)
            {
                read.Add(new UnlistedType(use, Standing.Unchanged, bcl));
                continue;
            }

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
