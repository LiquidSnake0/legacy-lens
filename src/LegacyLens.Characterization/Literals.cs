using System.Collections.Concurrent;
using System.Reflection;
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
///
/// There are two ways in and one set of rules. <see cref="From"/> reads a file
/// that has not been compiled, which is what a rewrite comparison has. <see
/// cref="In"/> reads what was compiled, which is all a characterization run
/// has: it is handed an assembly, and the source it came from may not be on
/// this machine at all. Both end in the same ranking, because the ranking is
/// where the two bugs were.
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

        var gathered = new Gathered();

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            gathered.Take(literal.Token.Value);

        return gathered.Ranked();
    }

    /// <summary>
    /// The values a compiled type mentions, read out of what the compiler
    /// emitted.
    ///
    /// A characterization run is handed an assembly and nothing else. The
    /// source it was built from may be on another machine, may have moved on
    /// since, and on a legacy codebase may not be anywhere anybody can find. So
    /// the constants are read where they certainly are, which is the code
    /// itself: `if (years >= 3)` compiles to an instruction that carries the 3,
    /// and that instruction is in the assembly being characterized.
    ///
    /// Read per type rather than per method, which is the same granularity
    /// <see cref="From"/> has. A boundary written in a constructor, a property
    /// or a private helper belongs to every method of the type that can reach
    /// it, and a method is rarely the unit somebody wrote the number in.
    ///
    /// Cached, because a run asks this once per target and a type answers the
    /// same thing every time.
    /// </summary>
    public static IReadOnlyDictionary<Type, IReadOnlyList<object?>> In(Type type) =>
        Remembered.GetOrAdd(type, Read);

    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<Type, IReadOnlyList<object?>>>
        Remembered = new();

    private static IReadOnlyDictionary<Type, IReadOnlyList<object?>> Read(Type type)
    {
        const BindingFlags Everything =
            BindingFlags.Public | BindingFlags.NonPublic
          | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var gathered = new Gathered();

        // A const field carries its value in metadata rather than in any
        // instruction, so a class whose boundary is `const int Minimum = 3`
        // would otherwise mention nothing at all.
        foreach (var field in type.GetFields(Everything).Where(f => f.IsLiteral))
        {
            try
            {
                gathered.Take(field.GetRawConstantValue());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A field whose constant cannot be read is not a reason to
                // report nothing for the type.
            }
        }

        IEnumerable<MethodBase> bodies = type.GetMethods(Everything);
        bodies = bodies.Concat(type.GetConstructors(Everything));

        foreach (var method in bodies)
        foreach (var constant in Il.Constants(method))
            gathered.Take(constant);

        return gathered.Ranked();
    }

    /// <summary>
    /// Everything found, before it is ranked.
    ///
    /// One accumulator for both readers. The rules that live here, the
    /// neighbours and the length cap and the ordering, were each written after
    /// a run got something wrong, and a second copy of them would drift.
    /// </summary>
    private sealed class Gathered
    {
        private readonly List<long> _numbers = [];
        private readonly List<decimal> _reals = [];
        private readonly List<string> _strings = [];

        public void Take(object? value)
        {
            switch (value)
            {
                case bool: break;            // true and false are not boundaries
                case byte number: Neighbours(_numbers, number); break;
                case sbyte number: Neighbours(_numbers, number); break;
                case short number: Neighbours(_numbers, number); break;
                case ushort number: Neighbours(_numbers, number); break;
                case int number: Neighbours(_numbers, number); break;
                case uint number: Neighbours(_numbers, number); break;
                case long number: Neighbours(_numbers, number); break;
                case ulong number when number <= long.MaxValue: Neighbours(_numbers, (long)number); break;
                case decimal number: _reals.Add(number); break;
                case float number when Sane(number): _reals.Add((decimal)number); break;
                case double number when Sane(number): _reals.Add((decimal)number); break;
                case string text when text.Length <= LongestString: _strings.Add(text); break;
            }
        }

        public IReadOnlyDictionary<Type, IReadOnlyList<object?>> Ranked()
        {
            var found = new Dictionary<Type, IReadOnlyList<object?>>();

            Add(found, typeof(int),
                _numbers.Where(n => n is >= int.MinValue and <= int.MaxValue).Select(n => (object?)(int)n),
                v => Math.Abs((long)(int)v!));

            Add(found, typeof(long), _numbers.Select(n => (object?)n), v => Math.Abs((long)v!));
            Add(found, typeof(decimal), _reals.Select(n => (object?)n), v => (long)Math.Abs((decimal)v!));
            Add(found, typeof(double), _reals.Select(n => (object?)(double)n), v => (long)Math.Abs((double)v!));
            Add(found, typeof(string), _strings.Select(s => (object?)s), v => ((string)v!).Length);

            return found;
        }
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
        // Spent on values that add something. Reading a compiled type turns up
        // every 0 and 1 the compiler emitted for a loop counter or a boolean
        // return, and those rank first because they are the most mentioned
        // thing in any method body. They are also the first values this tool
        // invents, so they are dropped downstream anyway: kept here they would
        // spend the whole allowance saying what was already going to be tried,
        // and push the one number the code actually turns on past the end.
        var invented = new HashSet<object?>(Values.For(type));

        var kept = values
            .Where(v => !invented.Contains(v))
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => size(g.Key))
            .Select(g => g.Key)
            .Take(PerKind)
            .ToList();

        if (kept.Count > 0) found[type] = kept;
    }
}
