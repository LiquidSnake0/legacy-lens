using System.Reflection;

namespace LegacyLens.Characterization;

/// <summary>A method this tool is willing to call.</summary>
public record Target(MethodInfo Method)
{
    public string Display =>
        $"{Method.DeclaringType?.FullName}.{Method.Name}("
      + string.Join(", ", Method.GetParameters().Select(p => p.ParameterType.Name))
      + ")";
}

/// <summary>
/// A member that was looked at and passed over, with the reason.
///
/// The reasons are the point. A run that produces four tests and skips six
/// hundred methods has said almost nothing until it says why, and the why is
/// what tells someone whether this technique reaches their codebase at all.
/// </summary>
public record Skipped(string Member, SkipReason Reason, string? Detail = null);

public enum SkipReason
{
    /// <summary>Its parameters are not types this tool can invent values for.</summary>
    ParameterTypeNotSupported,

    /// <summary>Returns void, so there is nothing to observe but side effects.</summary>
    NothingToObserve,

    /// <summary>Needs an instance, and the type cannot be built without arguments.</summary>
    NotConstructible,

    /// <summary>A property accessor, an operator, or something else generated.</summary>
    NotAPlainMethod,

    /// <summary>Two identical calls returned different results.</summary>
    NotDeterministic,

    /// <summary>Did not return within the time allowed.</summary>
    TooSlow,

    /// <summary>The result could not be turned into a value a test can compare.</summary>
    ResultNotComparable,

    /// <summary>The generated test did not compile, or failed when run.</summary>
    FailedItsOwnCheck,

    /// <summary>
    /// A type in its signature, or one it reaches when called, lives in an
    /// assembly that is not present.
    ///
    /// This is the environment being incomplete rather than the code doing
    /// anything, and the distinction matters: a missing assembly surfaces as a
    /// FileNotFoundException, which is indistinguishable from real behaviour
    /// unless it is separated out here. Pinning it would write a test asserting
    /// that the code throws, when all it does is that it was not deployed.
    /// </summary>
    DependencyMissing,
}

/// <summary>
/// Finds the methods worth pointing this at, inside an assembly that is already
/// built.
///
/// Working from a compiled assembly rather than from source is a deliberate
/// trade, and it is the opposite of the one the rest of this repository makes.
/// Everything else here reads code that does not build, because that is the
/// state inherited code is in. A characterization test cannot be one of those
/// things: it records what the code *does*, and nothing can observe that
/// without running it.
/// </summary>
public class TargetFinder
{
    /// <summary>
    /// Types whose values this tool knows how to invent and to write back out
    /// as a C# literal. Anything else is skipped and counted rather than
    /// guessed at, because a value invented for a type nobody modelled is how a
    /// generated test starts asserting nonsense.
    /// </summary>
    public static bool CanSupply(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;

        return actual.IsEnum
            || actual == typeof(string)
            || actual == typeof(bool)
            || actual == typeof(char)
            || actual == typeof(byte) || actual == typeof(sbyte)
            || actual == typeof(short) || actual == typeof(ushort)
            || actual == typeof(int) || actual == typeof(uint)
            || actual == typeof(long) || actual == typeof(ulong)
            || actual == typeof(float) || actual == typeof(double)
            || actual == typeof(decimal);
    }

    /// <summary>
    /// Looks through an assembly, optionally narrowed to certain types.
    ///
    /// The filter is what makes this usable on a real assembly: pointing it at
    /// everything is how a first run takes minutes and produces a file per
    /// class, when the whole point is to put a net under the handful of files
    /// the risk ranking named.
    /// </summary>
    public (IReadOnlyList<Target> Targets, IReadOnlyList<Skipped> Skipped) Find(
        Assembly assembly, Func<Type, bool>? include = null)
    {
        var targets = new List<Target>();
        var skipped = new List<Skipped>();

        foreach (var type in Types(assembly).Where(t => include is null || include(t)))
        {
            MethodInfo[] methods;

            try
            {
                methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                  | BindingFlags.DeclaredOnly);
            }
            catch (Exception exception) when (IsMissingDependency(exception))
            {
                // Listing the members of a type whose base class or interface
                // is missing fails as a whole, before any single method can be
                // considered on its own.
                skipped.Add(new Skipped(type.Name, SkipReason.DependencyMissing, exception.Message));
                continue;
            }

            foreach (var method in methods) Consider(method, targets, skipped);
        }

        return (targets, skipped);
    }

    /// <summary>
    /// True for the exceptions that mean "this assembly is not fully here".
    ///
    /// Reflection resolves signatures lazily, so an assembly loads happily and
    /// then throws the moment anyone asks for a return type it cannot find.
    /// MSBuild.Community.Tasks does exactly that: it loads, and reading its
    /// first method's return type raises FileNotFoundException for
    /// Microsoft.Build.Framework.
    /// </summary>
    internal static bool IsMissingDependency(Exception exception) =>
        exception is FileNotFoundException or TypeLoadException
                  or FileLoadException or BadImageFormatException;

    private static void Consider(MethodInfo method, List<Target> targets, List<Skipped> skipped)
    {
        var name = $"{method.DeclaringType?.Name}.{method.Name}";

        try
        {
            Inspect(method, name, targets, skipped);
        }
        catch (Exception exception) when (IsMissingDependency(exception))
        {
            skipped.Add(new Skipped(name, SkipReason.DependencyMissing, exception.Message));
        }
    }

    private static void Inspect(
        MethodInfo method, string name, List<Target> targets, List<Skipped> skipped)
    {

        // Property accessors, operators and compiler-generated members. Their
        // behaviour belongs to the property or the type, not to a method
        // somebody would think to characterise.
        if (method.IsSpecialName || method.IsAbstract || method.IsGenericMethodDefinition)
        {
            skipped.Add(new Skipped(name, SkipReason.NotAPlainMethod));
            return;
        }

        if (method.ReturnType == typeof(void))
        {
            skipped.Add(new Skipped(name, SkipReason.NothingToObserve));
            return;
        }

        var unsupported = method.GetParameters()
            .Where(p => !CanSupply(p.ParameterType))
            .Select(p => p.ParameterType.Name)
            .ToList();

        if (unsupported.Count > 0)
        {
            skipped.Add(new Skipped(name, SkipReason.ParameterTypeNotSupported,
                string.Join(", ", unsupported.Distinct())));
            return;
        }

        // An instance method needs something to be called on. A parameterless
        // constructor is the only case where this tool can supply that without
        // deciding what a valid object looks like, which is a decision about
        // the domain rather than about the code.
        if (!method.IsStatic && !HasParameterlessConstructor(method.DeclaringType))
        {
            skipped.Add(new Skipped(name, SkipReason.NotConstructible,
                method.DeclaringType?.Name));
            return;
        }

        targets.Add(new Target(method));
    }

    private static bool HasParameterlessConstructor(Type? type) =>
        type is { IsAbstract: false, IsInterface: false }
        && type.GetConstructor(Type.EmptyTypes) is not null;

    /// <summary>
    /// Public types the assembly declares itself.
    ///
    /// <see cref="ReflectionTypeLoadException"/> is expected rather than
    /// exceptional here: an assembly built against a framework this process
    /// does not have loads partially, and the types that did load are still
    /// worth working with. That partial failure is the whole finding when this
    /// is pointed at .NET Framework code.
    /// </summary>
    private static IEnumerable<Type> Types(Assembly assembly)
    {
        Type?[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types;
        }

        return types
            .OfType<Type>()
            .Where(t => t is { IsPublic: true, IsInterface: false })
            .Where(t => !t.Name.StartsWith('<'));
    }
}
