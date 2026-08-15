using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;

namespace LegacyLens.Characterization;

/// <summary>
/// Invents argument values, and writes them back out as C# a test can contain.
///
/// The values are fixed rather than random. A generated test has to be
/// reproducible, and a run that produces a different suite each time is a
/// suite nobody can review or commit.
/// </summary>
public static class Values
{
    /// <summary>
    /// Candidate values per type, ordered so that the ones most likely to
    /// expose a boundary come first: empty, zero, negative, then the extremes.
    /// </summary>
    public static IReadOnlyList<object?> For(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type);
        if (actual is not null)
        {
            // A nullable parameter gets null first: it is the case the author
            // either handled or did not, and it is the cheapest one to check.
            return new object?[] { null }.Concat(For(actual)).ToList();
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).Cast<object?>().Take(4).ToList();
        }

        if (type == typeof(string))
            return ["", "a", "  ", "hello world", null];

        if (type == typeof(bool))
            return [true, false];

        if (type == typeof(char))
            return ['a', ' ', '0'];

        if (type == typeof(int))
            return [0, 1, -1, 42, int.MaxValue, int.MinValue];

        if (type == typeof(long))
            return [0L, 1L, -1L, long.MaxValue, long.MinValue];

        if (type == typeof(short))
            return [(short)0, (short)1, (short)-1, short.MaxValue];

        if (type == typeof(ushort))
            return [(ushort)0, (ushort)1, ushort.MaxValue];

        if (type == typeof(byte))
            return [(byte)0, (byte)1, byte.MaxValue];

        if (type == typeof(sbyte))
            return [(sbyte)0, (sbyte)1, (sbyte)-1, sbyte.MaxValue];

        if (type == typeof(uint))
            return [0u, 1u, uint.MaxValue];

        if (type == typeof(ulong))
            return [0ul, 1ul, ulong.MaxValue];

        // No infinities and no NaN. Both are legitimate inputs and both write
        // out as expressions rather than literals, which would put a value in
        // the generated file that does not compile.
        if (type == typeof(double))
            return [0d, 1d, -1d, 0.5d, 1000d];

        if (type == typeof(float))
            return [0f, 1f, -1f, 0.5f];

        if (type == typeof(decimal))
            return [0m, 1m, -1m, 0.5m];

        return [];
    }

    /// <summary>
    /// One set of arguments per case, built by walking each parameter's values
    /// in step rather than by taking their product.
    ///
    /// The product of six parameters with five values each is 15,625 calls into
    /// code nobody has read. Walking in step gives every value of every
    /// parameter a turn, for a number of cases equal to the longest list.
    /// </summary>
    public static IReadOnlyList<object?[]> Cases(IReadOnlyList<Type> parameters, int limit)
    {
        if (parameters.Count == 0) return [[]];

        var columns = parameters.Select(For).ToList();
        if (columns.Any(c => c.Count == 0)) return [];

        var count = Math.Min(columns.Max(c => c.Count), limit);

        return Enumerable.Range(0, count)
            .Select(row => columns.Select(column => column[row % column.Count]).ToArray())
            .ToList();
    }

    /// <summary>
    /// A value as it has to be written to appear in a compiled test.
    ///
    /// Strings and chars go through Roslyn's own literal formatter: escaping
    /// them by hand is a source of bugs that only shows up on the one string in
    /// a codebase that contains a quote.
    /// </summary>
    public static string Literal(object? value) => value switch
    {
        null => "null",
        string text => SymbolDisplay.FormatLiteral(text, quote: true),
        char character => SymbolDisplay.FormatLiteral(character, quote: true),
        bool flag => flag ? "true" : "false",
        // "R" round-trips: the literal parses back to the same bits, which is
        // the only property that matters when the value is an assertion.
        double number => $"{number.ToString("R", CultureInfo.InvariantCulture)}d",
        float number => $"{number.ToString("R", CultureInfo.InvariantCulture)}f",
        decimal number => $"{number.ToString(CultureInfo.InvariantCulture)}m",
        long number => $"{number}L",
        ulong number => $"{number}UL",
        uint number => $"{number}u",
        // A cast rather than a suffix: C# has no literal suffix for these, and
        // an unsuffixed int does not implicitly convert in every position.
        short number => $"(short){number}",
        ushort number => $"(ushort){number}",
        byte number => $"(byte){number}",
        sbyte number => $"(sbyte){number}",
        int number => number.ToString(CultureInfo.InvariantCulture),
        Enum member => $"{Name(member.GetType())}.{member}",
        _ => throw new NotSupportedException($"No literal for {value.GetType().Name}."),
    };

    /// <summary>
    /// A type as it has to be written in a generated file.
    ///
    /// Fully qualified and rooted at <c>global::</c>. Generated code lands in a
    /// namespace it did not choose, next to types it has never seen, and a
    /// plain name that happens to match something in scope resolves to the
    /// wrong type without any error worth the name.
    /// </summary>
    public static string Name(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) return $"{Name(nullable)}?";

        // Nested types are written Outer+Inner by reflection and Outer.Inner by
        // the language.
        var full = type.FullName?.Replace('+', '.');

        return full is null ? type.Name : $"global::{full}";
    }
}
