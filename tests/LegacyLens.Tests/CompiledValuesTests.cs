using LegacyLens.Characterization;

namespace LegacyLens.Tests;

/// <summary>
/// Reading the boundaries out of what was compiled.
///
/// A characterization run is handed an assembly. The source it was built from
/// may be on another machine, may have moved on, and on an inherited codebase
/// may not be anywhere anybody can find. The constants are read where they
/// certainly are.
/// </summary>
public class CompiledValuesTests
{
    private static IReadOnlyList<object?> Offered<T>(Type wanted) =>
        Literals.In(typeof(T)).TryGetValue(wanted, out var found) ? found : [];

    private sealed class Boundary
    {
        public string Tier(int years) => years >= 3 ? "senior" : "junior";
    }

    private sealed class Declared
    {
        public const int Minimum = 250;

        public bool Reached(int amount) => amount >= Minimum;
    }

    private sealed class Compared
    {
        public int Role(string name) => name == "admin" ? 1 : 0;
    }

    private sealed class Noisy
    {
        public int Total(int n)
        {
            var sum = 0;

            for (var i = 0; i < n; i++)
            {
                if (i % 2 == 0) sum += 1;
                else sum -= 1;
            }

            return sum > 250 ? 250 : sum;
        }
    }

    [Fact]
    public void A_boundary_written_in_a_comparison_is_offered_back()
    {
        var offered = Offered<Boundary>(typeof(int));

        Assert.Contains(3, offered);
    }

    [Fact]
    public void And_the_two_either_side_of_it_come_with_it()
    {
        // An off-by-one lives beside a constant rather than on it: `>= 3`
        // rewritten as `> 3` agrees with the original at everything except 3,
        // and it is 2 and 4 that settle which side moved.
        var offered = Offered<Boundary>(typeof(int));

        Assert.Contains(2, offered);
        Assert.Contains(4, offered);
    }

    [Fact]
    public void A_boundary_that_is_a_const_field_carries_no_instruction_and_is_still_found()
    {
        // `const int Minimum = 250` is not loaded by any instruction: the
        // compiler folds it into the comparison as a literal, and the field
        // itself lives in metadata. Read only from method bodies, a class whose
        // boundary is a named constant would mention nothing.
        Assert.Contains(250, Offered<Declared>(typeof(int)));
    }

    [Fact]
    public void A_string_the_code_compares_against_is_offered_back()
    {
        Assert.Contains("admin", Offered<Compared>(typeof(string)));
    }

    [Fact]
    public void Nothing_offered_is_a_value_this_tool_was_going_to_try_anyway()
    {
        // The allowance is small and a compiled method body is full of zeroes
        // and ones: a loop counter, a boolean return, an empty accumulator.
        // They rank first because they are the most mentioned thing in any
        // method, and they are also the first values this tool invents, so
        // keeping them would spend the whole allowance saying what was already
        // going to be said.
        var invented = new HashSet<object?>(Values.For(typeof(int)));

        var offered = Offered<Noisy>(typeof(int));

        Assert.NotEmpty(offered);
        Assert.DoesNotContain(offered, value => invented.Contains(value));
        Assert.Contains(250, offered);
    }

    [Fact]
    public void The_two_readers_agree_about_the_same_code()
    {
        // The strongest thing that can be said about the instruction decoder,
        // and the reason it decodes rather than scans for bytes that look like
        // constants. An operand can hold any value, so a token containing 0x20
        // reads as `ldc.i4` to anything walking the stream a byte at a time.
        // Reading the source and reading what the compiler made of it are two
        // independent routes to one answer, and they have to arrive at it.
        var fromSource = Literals.From("""
            public sealed class Boundary
            {
                public string Tier(int years) => years >= 3 ? "senior" : "junior";
            }
            """);

        var compiled = Offered<Boundary>(typeof(int));

        foreach (var value in fromSource[typeof(int)])
            Assert.Contains(value, compiled);
    }

    [Fact]
    public void A_type_with_nothing_to_say_says_nothing()
    {
        Assert.Empty(Literals.In(typeof(Empty)));
    }

    private sealed class Empty
    {
        public object? Nothing() => null;
    }
}
