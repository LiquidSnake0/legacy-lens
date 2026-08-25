namespace LegacyLens.Analysis;

/// <summary>
/// One thing a reader has to know before applying a patch, and whether it is
/// theirs to decide.
///
/// The two are different and printing them the same way costs a reader more
/// than either of them saves. **A consequence** is something the conversion
/// did, and the reader checks it: build configurations dropped, items now
/// globbed from the folder. **A decision** is something nobody can do for
/// them: the solution targets 4.5.1 and PackageReference wants 4.6.1, so
/// somebody has to move it.
///
/// <see cref="About"/> is what makes the same caveat from thirty-one projects
/// one line instead of thirty-one. It is a key and never shown: the sentence is
/// what a person reads, and grouping by the sentence would break the moment one
/// of them carried a count.
/// </summary>
public record Caveat(string About, string Says)
{
    /// <summary>Something to choose, rather than something that was done.</summary>
    public bool Decides { get; init; }

    public override string ToString() => Says;
}

/// <summary>One caveat, and everyone who raised it.</summary>
public record Repeated(Caveat What, IReadOnlyList<string> Projects)
{
    /// <summary>True where the projects did not all say the same thing.</summary>
    public bool Varies { get; init; }
}

/// <summary>
/// Caveats, gathered so that the same one from many projects is one line.
///
/// This lived in the command that printed them, which made it look like a
/// concern of the terminal. It is not: the assessment has to group them too,
/// because the document somebody keeps is where a decision belongs, and two
/// groupings would eventually disagree about what counts as the same caveat.
/// </summary>
public static class Caveats
{
    /// <summary>
    /// The same caveat from many projects, said once.
    ///
    /// Grouped by <see cref="Caveat.About"/> rather than by the sentence,
    /// because the sentence carries counts and package names and no two
    /// projects write it the same way. Where the sentences do differ, one is
    /// shown and the line says so: a reader who is told twenty-nine projects
    /// said this needs to know whether they said the same thing.
    /// </summary>
    public static IReadOnlyList<Repeated> Group(IEnumerable<(string Project, Caveat Caveat)> raised)
    {
        return raised
            .GroupBy(entry => entry.Caveat.About, StringComparer.Ordinal)
            .Select(group =>
            {
                var texts = group.Select(entry => entry.Caveat.Says)
                    .Distinct(StringComparer.Ordinal).ToList();

                var projects = group.Select(entry => entry.Project)
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return new Repeated(group.First().Caveat, projects) { Varies = texts.Count > 1 };
            })
            .OrderByDescending(entry => entry.Projects.Count)
            .ToList();
    }
}
