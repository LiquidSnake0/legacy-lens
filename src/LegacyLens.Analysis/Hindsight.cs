namespace LegacyLens.Analysis;

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
    int FilesOnProposed)
{
    public Fate Became => (UsesAfter > 0, FilesOnProposed > 0) switch
    {
        (true, true) => Fate.Straddling,
        (true, false) => Fate.Kept,
        (false, true) => Fate.Ported,
        (false, false) => Fate.Dropped,
    };

    /// <summary>
    /// Whether the catalogue's number pointed the same way the team went.
    ///
    /// The hypothesis this whole comparison exists to test, stated so it can be
    /// counted rather than admired: a high coverage means the move is a
    /// substitution and a team will make it, a low one means it is a rewrite in
    /// disguise and they will not. Null where the catalogue proposed nothing,
    /// because a question that was never asked cannot be right or wrong.
    /// </summary>
    public bool? Agreed => Proposed is null
        ? null
        : Coverage >= Hindsight.Substitutable
            ? Became is Fate.Ported or Fate.Straddling
            : Became is Fate.Kept or Fate.Dropped;

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
    /// <summary>
    /// Above this share of covered calls, a move is a substitution rather than
    /// a rewrite wearing one.
    ///
    /// Not tuned. It is the midpoint, chosen before looking, so that the
    /// agreement rate below is a measurement and not a curve fitted to the
    /// answer it was supposed to produce.
    /// </summary>
    public const int Substitutable = 50;

    private readonly Surfaces _reading;

    public Hindsight(Surfaces? reading = null) => _reading = reading ?? new Surfaces();

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
                    name is not null && adopted.TryGetValue(name, out var files) ? files : 0);
            })
            .OrderByDescending(reckoning => reckoning.UsesBefore)
            .ToList();
    }

    /// <summary>
    /// How often the coverage number pointed the way the team went, over the
    /// dependencies the catalogue had an opinion about.
    ///
    /// Returns null when it had an opinion about none of them, because a rate
    /// over nothing is not a rate.
    /// </summary>
    public static (int Agreed, int Judged)? Agreement(IReadOnlyList<Reckoning> reckonings)
    {
        var judged = reckonings.Where(r => r.Agreed is not null).ToList();

        return judged.Count == 0 ? null : (judged.Count(r => r.Agreed == true), judged.Count);
    }
}
