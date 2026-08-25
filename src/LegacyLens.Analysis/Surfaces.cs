namespace LegacyLens.Analysis;

/// <summary>
/// What a codebase uses of its dependencies, as this tool answers it.
///
/// <see cref="ApiSurface"/> is the mechanism and takes the catalogue as an
/// argument, because it has to be able to abstain: asked about a package nobody
/// recorded, it must exclude nothing rather than guess. That argument used to
/// have a default, and a default is a second answer waiting to be given.
///
/// It was. The route built the catalogue and passed it; the command did not
/// pass anything and got the abstaining behaviour. On Orchard the route
/// answered **3,877 uses** of `Microsoft.AspNet.Mvc` and the command answered
/// **4,379** for the same directory: five hundred apart, thirteen per cent, and
/// the larger number is the one M13 was built to stop reporting, because it
/// counts types the framework still supplies as a dead package's work. The
/// README publishes 3,877, so the command contradicted the project's own
/// measurement.
///
/// The lesson M14 wrote down applies here without a word changed: **the same
/// program giving different answers depending on how it was asked is worse than
/// one that refuses to answer.** So this is the one way in. It loads the
/// catalogue once and applies it, every caller goes through it, and the
/// argument on the mechanism below has no default any more: a caller that means
/// to abstain now says so out loud.
/// </summary>
public sealed class Surfaces
{
    private readonly ApiSurface _reader = new();

    public Surfaces(SuccessorCatalogue? catalogue = null) =>
        Catalogue = catalogue ?? Successors.Load();

    public SuccessorCatalogue Catalogue { get; }

    /// <summary>
    /// The names the catalogue records as this package's.
    ///
    /// Without it a name the framework also has goes out with the package: a
    /// codebase using Newtonsoft's `JsonSerializer` would have it dropped
    /// because `System.Text.Json` has one too.
    /// </summary>
    public IReadOnlySet<string> Claimed(string package) =>
        Catalogue.For(package)
            .SelectMany(successor => successor.Types.Keys)
            .ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<UsageSurface> All(string rootPath) => _reader.All(rootPath, Claimed);

    public UsageSurface Of(string rootPath, string package) =>
        _reader.Of(rootPath, package, Claimed(package));

    /// <summary>What could replace it, best first.</summary>
    public IReadOnlyList<Coverage> Candidates(UsageSurface surface) =>
        new Successors().Rank(surface, Catalogue);
}
