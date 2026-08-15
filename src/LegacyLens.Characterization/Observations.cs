using System.Reflection;
using System.Text.Json;

namespace LegacyLens.Characterization;

/// <summary>How the result of a call can be written into an assertion.</summary>
public enum ResultShape
{
    /// <summary>A value C# can spell out: a number, a string, an enum, null.</summary>
    Literal,

    /// <summary>An object, compared through its serialised form.</summary>
    Json,

    /// <summary>The call threw, and the exception is the behaviour.</summary>
    Threw,
}

/// <summary>
/// What one call did, recorded well enough to assert on it later.
///
/// An exception is an observation like any other. Legacy code throws on inputs
/// its author never expected, and pinning that down is often more valuable than
/// pinning down the happy path: it is the behaviour most likely to be relied on
/// somewhere without anyone knowing.
/// </summary>
public record Observation(
    object?[] Arguments,
    ResultShape Shape,
    string Rendered,
    string? ExceptionType)
{
    /// <summary>
    /// Everything about the outcome, in one string, so that two runs can be
    /// compared without caring what type came back.
    /// </summary>
    public string Fingerprint => $"{Shape}|{ExceptionType}|{Rendered}";
}

/// <summary>
/// Calls a method and writes down what happened.
///
/// This is the part that runs code nobody in this process wrote. It is why the
/// whole capability is a command rather than an endpoint: pointing an HTTP API
/// at an arbitrary assembly and invoking it is remote code execution with extra
/// steps.
/// </summary>
public class Observer
{
    /// <summary>
    /// How long a single call is given. A method that has not returned by then
    /// is abandoned rather than waited out, because legacy code contains loops
    /// whose exit condition was an operator watching a screen.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs the call twice and returns the observation only if both runs agree.
    ///
    /// This is the cheapest reliable filter for the thing that would otherwise
    /// poison a generated suite: a method reading the clock, a GUID, the
    /// current culture or a random number produces a test that passes once and
    /// fails tomorrow morning. Two runs will not catch every case, and it
    /// catches every case anyone has hit in practice.
    /// </summary>
    public (Observation? Observation, SkipReason? Skipped) Observe(
        MethodInfo method, object? instance, object?[] arguments)
    {
        var (first, failure) = Once(method, instance, arguments);
        if (first is null) return (null, failure);

        var (second, _) = Once(method, instance, arguments);
        if (second is null || second.Fingerprint != first.Fingerprint)
            return (null, SkipReason.NotDeterministic);

        return (first, null);
    }

    private (Observation?, SkipReason?) Once(
        MethodInfo method, object? instance, object?[] arguments)
    {
        object? returned = null;
        Exception? thrown = null;

        // A thread that will not stop cannot be killed in .NET, so the timeout
        // abandons the wait rather than the work. The process this runs in is a
        // short-lived command, which is what makes that acceptable.
        var call = Task.Run(() =>
        {
            try
            {
                returned = method.Invoke(instance, arguments.ToArray());
            }
            catch (TargetInvocationException exception)
            {
                thrown = exception.InnerException ?? exception;
            }
            catch (Exception exception)
            {
                thrown = exception;
            }
        });

        if (!call.Wait(Timeout)) return (null, SkipReason.TooSlow);

        if (thrown is not null)
        {
            // An assembly that is not on this machine is not behaviour. Writing
            // it down would produce a test asserting that the code throws,
            // when what actually happened is that a dependency was not
            // deployed, and that test would fail on any machine where it is.
            if (TargetFinder.IsMissingDependency(thrown))
                return (null, SkipReason.DependencyMissing);

            return (new Observation(arguments, ResultShape.Threw,
                thrown.GetType().FullName ?? "Exception",
                thrown.GetType().FullName), null);
        }

        return Render(arguments, returned);
    }

    private static (Observation?, SkipReason?) Render(object?[] arguments, object? returned)
    {
        if (returned is null)
            return (new Observation(arguments, ResultShape.Literal, "null", null), null);

        if (TargetFinder.CanSupply(returned.GetType()))
        {
            return (new Observation(arguments, ResultShape.Literal,
                Values.Literal(returned), null), null);
        }

        // Anything else is pinned through its serialised form, which is the
        // golden-master trick: the test does not understand the object, it only
        // notices when it changes.
        try
        {
            var json = JsonSerializer.Serialize(returned);

            // A serialiser that produced nothing has not described anything,
            // and an assertion on "{}" passes for every object of that shape.
            if (json is "{}" or "[]" or "null")
                return (null, SkipReason.ResultNotComparable);

            return (new Observation(arguments, ResultShape.Json, json, null), null);
        }
        // Deliberately broad. This serialises objects nobody here designed, and
        // the failures are not limited to JsonException: a property whose type
        // is a pointer or a ref struct raises InvalidOperationException, and a
        // graph with a cycle can exhaust the stack. Every one of them means the
        // same thing, which is that this result cannot be pinned.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (null, SkipReason.ResultNotComparable);
        }
    }
}
