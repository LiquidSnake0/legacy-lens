using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LegacyLens.Characterization;

/// <summary>
/// The values a file mentions, offered back to it as arguments.
///
/// Invented values find the boundaries somebody thought of in advance: empty,
/// zero, negative, the extremes. They do not find the boundary this particular
/// code cares about, and that boundary is usually written down a few lines
/// away. A method reading `years >= 3` has told you the number that matters,
/// and a rewrite that turned it into `> 3` is invisible to any run that never
/// tried 3.
///
/// Measured on a rewrite that made exactly that change: without this the
/// comparison reported the method as unchanged over six calls, because it tried
/// 0, 1, -1, 42 and the two extremes and never the one number in the source.
///
/// Each number brings its neighbours. An off-by-one lives on one side of the
/// constant rather than on it, so 3 is worth little without 2 and 4.
/// </summary>
public static class Literals
{
    /// <summary>How many of each kind are kept. A file is not a corpus.</summary>
    private const int PerKind = 8;

    /// <summary>Longer strings are quoted prose or SQL, not a boundary.</summary>
    private const int LongestString = 40;

    /// <summary>
    /// Everything worth trying, grouped by the type it can be passed as.
    ///
    /// Returns an empty list rather than throwing on a file that will not
    /// parse: the caller is about to compile it and will report that properly.
    /// </summary>
    public static IReadOnlyDictionary<Type, IReadOnlyList<object?>> From(string source)
    {
        SyntaxNode root;

        try
        {
            root = CSharpSyntaxTree.ParseText(source).GetRoot();
        }
        catch (Exception)
        {
            return new Dictionary<Type, IReadOnlyList<object?>>();
        }

        var numbers = new List<long>();
        var reals = new List<decimal>();
        var strings = new List<string>();

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            switch (literal.Token.Value)
            {
                case int number: Neighbours(numbers, number); break;
                case long number: Neighbours(numbers, number); break;
                case decimal number: reals.Add(number); break;
                case double number when Sane(number): reals.Add((decimal)number); break;
                case string text when text.Length <= LongestString: strings.Add(text); break;
            }
        }

        var found = new Dictionary<Type, IReadOnlyList<object?>>();

        Add(found, typeof(int),
            numbers.Where(n => n is >= int.MinValue and <= int.MaxValue).Select(n => (object?)(int)n),
            v => Math.Abs((long)(int)v!));

        Add(found, typeof(long), numbers.Select(n => (object?)n), v => Math.Abs((long)v!));
        Add(found, typeof(decimal), reals.Select(n => (object?)n), v => (long)Math.Abs((decimal)v!));
        Add(found, typeof(double), reals.Select(n => (object?)(double)n), v => (long)Math.Abs((double)v!));
        Add(found, typeof(string), strings.Select(s => (object?)s), v => ((string)v!).Length);

        return found;
    }

    /// <summary>
    /// The value and the two either side of it.
    ///
    /// An off-by-one lives beside a constant rather than on it: a rewrite that
    /// turns `>= 3` into `> 3` agrees with the original at 3 only if 3 is not
    /// the value that separates them, and it is 2 and 4 that settle it.
    /// </summary>
    private static void Neighbours(List<long> into, long value)
    {
        into.Add(value);

        if (value > long.MinValue) into.Add(value - 1);
        if (value < long.MaxValue) into.Add(value + 1);
    }

    /// <summary>Infinities and NaN are legitimate values and not boundaries anyone wrote.</summary>
    private static bool Sane(double number) =>
        !double.IsNaN(number) && !double.IsInfinity(number)
        && number is > (double)decimal.MinValue and < (double)decimal.MaxValue;

    /// <summary>
    /// Keeps the few worth trying, most mentioned first and smallest after
    /// that.
    ///
    /// The tie-break is by size and not by how the value prints, which is the
    /// bug this comment exists for. Sorted as text, "100" comes before "3", so
    /// a file mentioning a page size and a boundary of three kept the page size
    /// and dropped the boundary: the comparison then reported an off-by-one
    /// rewrite as unchanged. Found by running it on a file with both.
    ///
    /// Smallest first is the right order for the same reason boundaries are
    /// small. A limit of 3, an index of 0, an empty string: those are what a
    /// rewrite gets wrong, and a magic number in the hundreds is usually
    /// configuration that happens to be spelled in the code.
    /// </summary>
    private static void Add(
        Dictionary<Type, IReadOnlyList<object?>> found,
        Type type,
        IEnumerable<object?> values,
        Func<object?, long> size)
    {
        var kept = values
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => size(g.Key))
            .Select(g => g.Key)
            .Take(PerKind)
            .ToList();

        if (kept.Count > 0) found[type] = kept;
    }
}
