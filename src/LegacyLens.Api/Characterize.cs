using System.Reflection;
using LegacyLens.Characterization;

namespace LegacyLens.Api;

/// <summary>
/// The command behind `characterize`.
///
/// Kept out of Program.cs because it is the one capability here that runs code
/// belonging to somebody else, and that deserves to be findable rather than
/// folded into a startup file.
/// </summary>
internal static class Characterize
{
    /// <summary>
    /// characterize &lt;assembly.dll&gt; [--out &lt;directory&gt;] [--type &lt;name&gt;]
    ///
    /// Both options are optional and independent: narrowing to a type without
    /// writing anything is how someone looks before they commit, and it is the
    /// order the tool should encourage.
    /// </summary>
    public static void Run(string assemblyPath, string[] options)
    {
        var outputDirectory = Option(options, "--out");
        var typeName = Option(options, "--type");

        // How many cases each method gets, and the one number here that costs
        // somebody something. Measured by mutation on a class with four
        // boundaries: four cases catch the ones the code names, ten catch all
        // four mutants and more than double the file. That is a trade for
        // whoever reads and commits the file rather than one to make for them,
        // so it is an option with the cheaper end as the default.
        var asked = Option(options, "--cases");
        var budget = Number(options, "--cases");

        // Asked for and not understood is refused rather than ignored. Falling
        // back to the default would produce a smaller file than the person
        // requested, with nothing anywhere saying it had.
        if (asked is not null && budget is not (> 0))
        {
            Console.Error.WriteLine("--cases has to be a positive whole number.");
            return;
        }

        var full = Path.GetFullPath(assemblyPath);

        if (!File.Exists(full))
        {
            Console.Error.WriteLine($"No such file: {full}");
            return;
        }

        Assembly subject;

        try
        {
            subject = Assembly.LoadFrom(full);
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
        {
            // The expected failure when this meets .NET Framework code, and the
            // most useful thing the command can say. Everything else in this
            // repository reads code that cannot run; this is the one part that
            // needs it to, and here is where that bill arrives.
            Console.Error.WriteLine($"Could not load {Path.GetFileName(full)} into this runtime.");
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Characterization runs the code, so the assembly has to load here. A .NET "
              + "Framework assembly generally will not on Linux: it needs the framework it "
              + "was built against, which means a Windows host.");
            return;
        }

        var run = new Characterizer
        {
            CasesPerMethod = budget ?? new Characterizer().CasesPerMethod,
            Types = typeName is null
                ? null
                : type => string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase),
        }.Run(subject);

        if (typeName is not null && run.MethodsConsidered == 0)
        {
            Console.Error.WriteLine($"No public type called {typeName} in this assembly.");
            return;
        }

        Report(run);

        if (outputDirectory is null)
        {
            Console.WriteLine("Nothing was written. Pass --out <directory> to keep the files.");
            return;
        }

        Directory.CreateDirectory(outputDirectory);

        foreach (var file in run.Files)
        {
            var path = Path.Combine(outputDirectory, file.FileName);
            File.WriteAllText(path, file.Source);
            Console.WriteLine($"wrote {path}");
        }
    }

    /// <summary>The value after a named option as a number, or null.</summary>
    private static int? Number(string[] options, string name) =>
        int.TryParse(Option(options, name), out var value) ? value : null;

    /// <summary>
    /// The value after a named option, or null. Deliberately minimal: a command
    /// with two switches does not need an argument-parsing library, and adding
    /// one would be the largest dependency in the project.
    /// </summary>
    private static string? Option(string[] options, string name)
    {
        var at = Array.IndexOf(options, name);
        return at >= 0 && at + 1 < options.Length ? options[at + 1] : null;
    }

    private static void Report(CharacterizationRun run)
    {
        Console.WriteLine($"Characterized {run.Assembly}");
        Console.WriteLine();
        Console.WriteLine($"  methods callable      {run.MethodsConsidered,6}");
        Console.WriteLine($"  calls made            {run.CallsMade,6}");
        Console.WriteLine($"  tests kept            {run.Tests,6}   "
                        + $"in {run.Files.Count} file(s)");
        Console.WriteLine($"  elapsed               {run.ElapsedMs,6} ms");
        Console.WriteLine();

        if (run.Skipped.Count == 0) return;

        // The refusals are the finding. A handful of tests out of hundreds of
        // methods means nothing until it says what stopped the rest.
        Console.WriteLine("Not characterized:");
        Console.WriteLine();

        foreach (var (reason, count) in run.Refusals)
            Console.WriteLine($"  {count,6}  {Reasons.Explain(reason)}");

        Console.WriteLine();
    }
}
