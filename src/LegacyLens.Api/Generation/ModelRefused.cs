namespace LegacyLens.Api.Generation;

/// <summary>
/// The model was reachable and would not answer.
///
/// Separate from a connection failure because the fix is different, and the
/// difference is the whole value of the message: Ollama running without the
/// model pulled, a hosted provider rejecting the key, a rate limit. Each of
/// those sends somebody to a different place, and none of them is "is it
/// running".
///
/// It exists because both clients already built that sentence and nothing ever
/// caught it. They threw <see cref="InvalidOperationException"/>, no endpoint
/// handled it, and the reader got a bare 500 with a stack trace: exactly the
/// outcome one of those clients has a comment saying it wants to avoid. Found
/// by running a projection against a machine where the default model had not
/// been pulled.
/// </summary>
/// <remarks>
/// Derived from <see cref="InvalidOperationException"/> rather than from
/// <see cref="Exception"/> so that every place already catching the old type
/// keeps catching this one. The streaming answer is one of them, and a new
/// exception type outside that hierarchy would have turned a reported failure
/// into an unhandled one on the path that is hardest to test.
/// </remarks>
public class ModelRefused : InvalidOperationException
{
    public ModelRefused(string message, string hint) : base(message) => Hint = hint;

    /// <summary>What to do about it, which is the part worth reading.</summary>
    public string Hint { get; }
}
