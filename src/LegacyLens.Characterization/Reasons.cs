namespace LegacyLens.Characterization;

/// <summary>
/// Why something was passed over, in words rather than in an enum name.
///
/// Kept here rather than beside each reader. The refusals are the honest half
/// of both a characterization run and an equivalence check, and two copies of
/// the wording would drift until the browser and the terminal described the
/// same refusal differently.
/// </summary>
public static class Reasons
{
    public static string Explain(SkipReason reason) => reason switch
    {
        SkipReason.ParameterTypeNotSupported =>
            "takes a parameter this tool cannot invent a value for",
        SkipReason.NothingToObserve =>
            "returns void, so only its side effects change anything",
        SkipReason.NotConstructible =>
            "needs an instance that cannot be built without arguments",
        SkipReason.NotAPlainMethod =>
            "a property accessor, an operator, or generated",
        SkipReason.NotDeterministic =>
            "two identical calls disagreed: a clock, a guid or a random number",
        SkipReason.TooSlow =>
            "did not return in time",
        SkipReason.ResultNotComparable =>
            "returned something no assertion can compare",
        SkipReason.FailedItsOwnCheck =>
            "the generated test did not compile or did not pass",
        SkipReason.DependencyMissing =>
            "needs an assembly that is not on this machine",
        SkipReason.ArgumentNotPortable =>
            "takes a type the file declares itself, so the same value cannot be given to both",
        SkipReason.NoCounterpart =>
            "the rewrite has no class of that name",
        SkipReason.SignatureChanged =>
            "gone from the rewrite, or its parameters changed",
        SkipReason.NothingLeftToCompare =>
            "every call was dropped by one side or the other, so it was never compared",
        SkipReason.BeyondTheLimit =>
            "past the number of methods one run attempts",
        _ => reason.ToString(),
    };
}
