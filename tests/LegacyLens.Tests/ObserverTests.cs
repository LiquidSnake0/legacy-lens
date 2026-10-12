using System.Reflection;
using LegacyLens.Characterization;

namespace LegacyLens.Tests;

/// <summary>
/// The observer's clock is wall-clock time, and the first call into a method
/// pays for its JIT on whatever machine runs the suite. These tests pin what
/// happens when that first call is slow and the next ones are not.
/// </summary>
public class ObserverTests
{
    public static class Subject
    {
        private static int _slowOnceCalls;

        /// <summary>Slow the first time it is ever called, instant after that.</summary>
        public static int SlowOnce(int value)
        {
            if (Interlocked.Increment(ref _slowOnceCalls) == 1) Thread.Sleep(400);
            return value * 2;
        }

        /// <summary>Slow every time.</summary>
        public static int AlwaysSlow(int value)
        {
            Thread.Sleep(400);
            return value * 2;
        }
    }

    private static MethodInfo Method(string name) =>
        typeof(Subject).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    [Fact]
    public void A_call_that_is_slow_only_the_first_time_is_still_observed()
    {
        var observer = new Observer { Timeout = TimeSpan.FromMilliseconds(100) };

        var (observation, skipped) = observer.Observe(Method("SlowOnce"), null, [21]);

        Assert.Null(skipped);
        Assert.NotNull(observation);
        Assert.Equal("42", observation.Rendered);
    }

    [Fact]
    public void A_call_that_is_always_slow_is_still_refused()
    {
        var observer = new Observer { Timeout = TimeSpan.FromMilliseconds(100) };

        var (observation, skipped) = observer.Observe(Method("AlwaysSlow"), null, [21]);

        Assert.Null(observation);
        Assert.Equal(SkipReason.TooSlow, skipped);
    }
}
