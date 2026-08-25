namespace LegacyLens.Analysis;

/// <summary>One recorded correspondence, held against what a team actually wrote.</summary>
public record Correspondence(
    string Package,
    string Type,
    int Uses,
    /// <summary>What the catalogue records, or null where it records nothing for this name.</summary>
    string? Recorded,
    /// <summary>The catalogue records that nothing does its job. A fact, not a gap.</summary>
    bool RecordedAsNone,
    /// <summary>The recorded counterpart turns up in the finished code.</summary>
    bool CounterpartSeen,
    /// <summary>A type of the same name turns up under the successor.</summary>
    bool SameNameSeen,
    /// <summary>The framework confirms a type of that name inside the successor's namespaces.</summary>
    string? InSuccessor = null)
{
    /// <summary>
    /// A correspondence the catalogue does not have and the finished code does.
    ///
    /// The old code used this name, nobody wrote down what replaces it, the
    /// finished code has one of the same name where the successor is imported,
    /// **and the framework confirms a type of that name inside the successor's
    /// own namespaces**.
    ///
    /// The last condition is what makes this a measurement rather than a guess,
    /// and it is not decoration. A file importing two packages cannot be split
    /// between them by syntax alone, so the first two conditions on their own
    /// offered `SqlConnection`, `PayPalException` and `ExcelWorksheet` as
    /// correspondences for ASP.NET MVC. Asking the framework whether
    /// `Microsoft.AspNetCore.Mvc` actually contains a `SqlConnection` answers
    /// that in a way nothing about the source text can.
    /// </summary>
    public bool Candidate =>
        Recorded is null && !RecordedAsNone && SameNameSeen && InSuccessor is not null;
}

/// <summary>
/// How the catalogue's type correspondences did against real migrations.
///
/// The package question came first: does this dependency move at all. This is
/// the next one down. When the catalogue says `ActionResult` becomes
/// `IActionResult`, is that what four teams actually wrote?
///
/// **A counterpart that does not turn up is not a wrong entry.** The team may
/// simply never have needed it. So the two are reported apart and the second is
/// never called an error: confirmed means the recorded name is in the finished
/// code, unseen means it is not, and unseen is evidence rather than a verdict.
///
/// The other half is what the catalogue is missing. It is hand-written, which
/// is right for a judgement and slow for transcription, and most of a framework
/// move is transcription: the type kept its name and changed namespace. Four
/// real migrations know hundreds of those. Read out, they are candidates for a
/// person to sign, and never entries written back into the catalogue by a
/// machine.
/// </summary>
public sealed class Correspondences
{
    private readonly Surfaces _reading;
    private readonly SuccessorCatalogue _catalogue;

    public Correspondences(Surfaces? reading = null)
    {
        _reading = reading ?? new Surfaces();
        _catalogue = _reading.Catalogue;
    }

    public IReadOnlyList<Correspondence> Compare(string before, string after)
    {
        if (!Directory.Exists(before)) throw new DirectoryNotFoundException($"No such directory: {before}");
        if (!Directory.Exists(after)) throw new DirectoryNotFoundException($"No such directory: {after}");

        var was = _reading.All(before);

        // The successor of each package, and what names live under it in the
        // finished code. One read of the tree for all of them.
        var successors = was.ToDictionary(
            surface => surface.Package,
            surface => _reading.Candidates(surface).FirstOrDefault()?.Candidate,
            StringComparer.OrdinalIgnoreCase);

        var names = new ApiSurface().NamesUnder(
            after,
            successors.Values.OfType<string>().Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal).ToList());

        var found = new List<Correspondence>();

        foreach (var surface in was)
        {
            var successor = successors[surface.Package];
            if (successor is not { Length: > 0 }) continue;

            var written = names.TryGetValue(successor, out var under) ? under : new HashSet<string>();

            // Whether the framework itself carries the successor at all. Where
            // it does not, nothing below can be confirmed and nothing is
            // offered: a package this runtime has never loaded cannot be asked
            // what it contains.
            var carried = FrameworkTypes.Carries(successor);

            var entry = _catalogue.For(surface.Package)
                .FirstOrDefault(candidate => candidate.Package == successor);

            foreach (var use in surface.Types)
            {
                var recorded = entry is not null && entry.Types.TryGetValue(use.Name, out var to)
                    ? to
                    : null;

                // A type mapped to null in the catalogue is a recorded fact:
                // nothing in the new package does its job. Not the same as a
                // name nobody has written down, and folding the two together is
                // how a decision becomes an oversight.
                var none = entry is not null
                        && entry.Types.TryGetValue(use.Name, out var mapped) && mapped is null;

                found.Add(new Correspondence(
                    surface.Package,
                    use.Name,
                    use.Uses,
                    recorded,
                    none,
                    recorded is not null && Seen(written, recorded),
                    Seen(written, use.Name),
                    carried ? FrameworkTypes.Under(use.Name, successor).FirstOrDefault() : null));
            }
        }

        return found.OrderByDescending(c => c.Uses).ToList();
    }

    /// <summary>
    /// Whether a name turns up, under either of the two spellings an attribute
    /// answers to.
    ///
    /// The catalogue records `HttpPost` becoming `HttpPostAttribute`, which is
    /// the declared name, and nobody writes it: a use is `[HttpPost]` and the
    /// reader records the short spelling for that reason. Compared literally,
    /// six hundred and eight uses of the commonest attribute in ASP.NET MVC
    /// came back as a correspondence nobody had taken up.
    /// </summary>
    private static bool Seen(IReadOnlySet<string> written, string name)
    {
        if (written.Contains(name)) return true;

        const string Suffix = "Attribute";

        return name.EndsWith(Suffix, StringComparison.Ordinal)
            && name.Length > Suffix.Length
            && written.Contains(name[..^Suffix.Length]);
    }

    /// <summary>
    /// How many recorded correspondences turned up, over how many were
    /// exercised by the old code.
    ///
    /// Only the ones the old code actually used. Marking an entry nobody
    /// touched would be marking the catalogue on breadth rather than on being
    /// right.
    /// </summary>
    public static (int Confirmed, int Recorded) Mark(IReadOnlyList<Correspondence> correspondences)
    {
        var asserted = correspondences.Where(c => c.Recorded is not null).ToList();

        return (asserted.Count(c => c.CounterpartSeen), asserted.Count);
    }
}
