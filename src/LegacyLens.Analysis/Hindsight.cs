namespace LegacyLens.Analysis;

/// <summary>
/// How each half of the prediction did, kept apart on purpose.
///
/// One half says what the runtime leaves no choice about, and it is a
/// prediction. The other says what a team is free to do either way, and it is
/// not. Blending them produces a number that is true of neither.
/// </summary>
public record Marking(int ForcedHeld, int Forced, int ChosenHeld, int Chosen)
{
    public bool Anything => Forced + Chosen > 0;
}

/// <summary>What a team did about one dependency, once they had finished.</summary>
public enum Fate
{
    /// <summary>Gone from the new code, and the successor this tool proposes is there instead.</summary>
    Ported,

    /// <summary>Gone, but not in favour of what was proposed. They chose something else, or the need went away.</summary>
    Dropped,

    /// <summary>Still used. They decided it was not worth moving, or not yet.</summary>
    Kept,

    /// <summary>Both are in the new code. A migration caught in the middle, which is what an incremental one looks like.</summary>
    Straddling,
}

/// <summary>
/// One dependency, read against what actually happened to it.
/// </summary>
public record Reckoning(
    string Package,
    int UsesBefore,
    int TypesBefore,
    int FilesBefore,
    int UsesAfter,
    /// <summary>What the catalogue proposes, or null where it has nothing to say.</summary>
    string? Proposed,
    /// <summary>The share of the calls that proposal covers, as the catalogue records it.</summary>
    int Coverage,
    /// <summary>Files in the finished code importing that proposal.</summary>
    int FilesOnProposed,
    /// <summary>Whether anybody recorded that this package cannot stay. Null where nobody did.</summary>
    bool? Strands = null)
{
    public Fate Became => (UsesAfter > 0, FilesOnProposed > 0) switch
    {
        (true, true) => Fate.Straddling,
        (true, false) => Fate.Kept,
        (false, true) => Fate.Ported,
        (false, false) => Fate.Dropped,
    };

    /// <summary>
    /// Whether the package is still in the finished code.
    ///
    /// The fact the prediction is marked against. A migration caught in the
    /// middle is judged by weight rather than by presence: a stranded package
    /// on its way out leaves a shrinking remainder, and a library somebody kept
    /// does not shrink. Umbraco's MVC went from 1,106 uses to 18 and is gone in
    /// every sense that matters; Smartstore's Newtonsoft went from 1,045 to
    /// 1,572 with two files on the successor, and calling that a move would be
    /// reading a toe in the water as a decision.
    /// </summary>
    public bool StillThere => Became switch
    {
        Fate.Kept or Fate.Straddling => UsesAfter * Remainder > UsesBefore,
        _ => false,
    };

    /// <summary>
    /// What counts as a remainder rather than a dependency.
    ///
    /// A fifth, and the number barely matters, which is the point. Across three
    /// real ports the packages on their way out were left at 1.6, 2.5 and 11
    /// per cent of their former usage, and the ones the teams kept were at 78
    /// per cent or more, several of them well over a hundred because they were
    /// used harder afterwards. Nothing measured falls between eleven and
    /// seventy-eight, so anything chosen inside that gap gives the same answer
    /// and this is not a threshold anybody has to defend to the decimal.
    /// </summary>
    private const int Remainder = 5;

    /// <summary>
    /// Whether it was expected to still be there, or null where nothing was claimed.
    ///
    /// A package that cannot exist on the target is expected to be gone. One
    /// that runs there unchanged is expected to stay, because nothing forces
    /// the move and teams keep what works. That is the whole prediction, and it
    /// is deliberately not the coverage number.
    /// </summary>
    public bool? Expected => Strands is null ? null : !Strands.Value;

    /// <summary>
    /// Whether the prediction held. Null where none was made, because a
    /// question that was never asked cannot be right or wrong.
    /// </summary>
    public bool? Agreed => Expected is null ? null : Expected == StillThere;

    public string Claim => Became switch
    {
        Fate.Ported =>
            $"gone, and {Proposed} is in the finished code across {FilesOnProposed} file(s)",
        Fate.Straddling =>
            $"both: {UsesAfter} use(s) left and {Proposed} across {FilesOnProposed} file(s)",
        Fate.Kept => $"still used, {UsesAfter} time(s)",
        _ => "gone, and not in favour of what was proposed",
    };
}

/// <summary>
/// A finished migration, read backwards.
///
/// Nobody can evaluate a migration tool, because there is no correct answer to
/// compare it against. That is why this whole field is sold on adjectives.
///
/// A codebase that exists in both states removes the problem. nopCommerce 3.90
/// is 31 projects on .NET Framework 4.5.1, 425 files importing `System.Web`,
/// and nothing importing ASP.NET Core. Version 4.00 is the same product after
/// the port: 26 SDK-style projects, two files left on `System.Web`, 416 on
/// ASP.NET Core, and three per cent more lines, which is the signature of a
/// port rather than a rewrite. What the team decided is a matter of record.
///
/// So the tool can be marked. For every dependency it has an opinion about, it
/// said something before the fact, and the finished code says what happened.
/// The interesting number is not how many it got right; it is whether the
/// number it prints, the share of calls a successor covers, points the same way
/// the people went.
///
/// **This reads syntax on both sides and compiles nothing**, so it works on a
/// legacy tree that does not build, which is every legacy tree.
/// </summary>
public sealed class Hindsight
{
    private readonly Surfaces _reading;
    private readonly Strandings _strandings;

    public Hindsight(Surfaces? reading = null, Strandings? strandings = null)
    {
        _reading = reading ?? new Surfaces();
        _strandings = strandings ?? Strandings.Load();
    }

    /// <summary>Where the judgement about what can stay came from.</summary>
    public string Catalogue => _strandings.Source;

    /// <summary>
    /// Every catalogued dependency the old code used, and what became of it.
    ///
    /// Ordered by how much of the old code each one held, because that is the
    /// order in which the decisions mattered.
    /// </summary>
    public IReadOnlyList<Reckoning> Compare(string before, string after)
    {
        if (!Directory.Exists(before)) throw new DirectoryNotFoundException($"No such directory: {before}");
        if (!Directory.Exists(after)) throw new DirectoryNotFoundException($"No such directory: {after}");

        var was = _reading.All(before);
        var now = _reading.All(after).ToDictionary(s => s.Package, StringComparer.OrdinalIgnoreCase);

        var proposals = was.ToDictionary(
            surface => surface.Package,
            surface => _reading.Candidates(surface).FirstOrDefault(),
            StringComparer.OrdinalIgnoreCase);

        // One read of the finished tree for every proposal, rather than one per
        // package. A proposal's name is its root namespace on modern .NET.
        var adopted = new ApiSurface().Importing(
            after,
            proposals.Values.OfType<Coverage>()
                .Select(candidate => candidate.Candidate)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList());

        return was
            .Select(surface =>
            {
                var proposal = proposals[surface.Package];
                var name = proposal?.Candidate is { Length: > 0 } named ? named : null;

                return new Reckoning(
                    surface.Package,
                    surface.Uses,
                    surface.Types.Count,
                    surface.Files,
                    now.TryGetValue(surface.Package, out var after_) ? after_.Uses : 0,
                    name,
                    proposal?.Percent ?? 0,
                    name is not null && adopted.TryGetValue(name, out var files) ? files : 0,
                    _strandings.For(surface.Package)?.Strands);
            })
            .OrderByDescending(reckoning => reckoning.UsesBefore)
            .ToList();
    }

    /// <summary>
    /// How often each half of the prediction held, and never the two blended.
    ///
    /// Measured across four real migrations, twenty-seven predictions: what the
    /// runtime forces held **fifteen times out of fifteen**, and what a team
    /// merely could do held six times out of twelve. A single rate of
    /// twenty-one out of twenty-seven would hide the only thing worth knowing,
    /// which is that one half is certain and the other is not a prediction at
    /// all.
    ///
    /// The split in the discretionary half is not noise either. In the three
    /// ports it was six of eight, because a port keeps what still runs. In the
    /// one rewrite it was none of four, because a rewrite keeps nothing: new
    /// code picks new libraries.
    /// </summary>
    public static Marking Mark(IReadOnlyList<Reckoning> reckonings)
    {
        var forced = reckonings.Where(r => r.Strands == true).ToList();
        var chosen = reckonings.Where(r => r.Strands == false).ToList();

        return new Marking(
            forced.Count(r => r.Agreed == true), forced.Count,
            chosen.Count(r => r.Agreed == true), chosen.Count);
    }
}
