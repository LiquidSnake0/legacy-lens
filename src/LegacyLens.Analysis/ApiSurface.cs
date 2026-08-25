using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LegacyLens.Analysis;

/// <summary>One type from a package, and how much of the codebase touches it.</summary>
public record ApiUse(string Name, int Uses, int Files);

/// <summary>
/// One file, and how much of the package it leans on.
///
/// Named rather than counted, because the next question after "how much work
/// is this" is always "show me one", and a projection needs a path.
/// </summary>
public record FileUse(string Path, int Uses, int Lines)
{
    /// <summary>
    /// Calls per hundred lines.
    ///
    /// What makes a file worth showing, rather than its total. Orchard's
    /// heaviest user of ASP.NET MVC is 821 lines, which a local model spends
    /// minutes on and nobody reads in a browser. A short file with the same
    /// correspondences teaches the same lesson in a screen.
    /// </summary>
    public int Density => Lines == 0 ? 0 : Uses * 100 / Lines;
}

/// <summary>What a codebase actually uses of one package.</summary>
public record UsageSurface(
    string Package,
    IReadOnlyList<string> Namespaces,
    /// <summary>Types named in the files that import it, most used first.</summary>
    IReadOnlyList<ApiUse> Types,
    /// <summary>Files that import one of its namespaces.</summary>
    int Files,
    IReadOnlyList<string> Notes,
    /// <summary>
    /// The files worth looking at first, densest first.
    ///
    /// Ranked by calls per line rather than by calls, and that is a correction
    /// rather than a preference. These rewrites are repetitive, so what a
    /// reader needs is the file that shows the most correspondences in the
    /// fewest lines, not the one with the largest total. The largest here is
    /// 821 lines, which a local model spends minutes on and nobody reads.
    ///
    /// Files too long to project at all are left out entirely, with a note,
    /// rather than offered and then refused.
    /// </summary>
    IReadOnlyList<FileUse> Heaviest)
{
    public int Uses => Types.Sum(t => t.Uses);
    public bool Used => Files > 0;

    /// <summary>
    /// How many types account for four fifths of the calls.
    ///
    /// The number that decides the shape of the work. Six types carrying
    /// everything is an afternoon of find-and-replace; sixty spread evenly is a
    /// rewrite, and the two are indistinguishable from a total.
    /// </summary>
    public int TypesForMostOfIt => ConcentrationOf(Types.Select(t => t.Uses));

    /// <summary>The same question asked of files rather than of types.</summary>
    public int FilesForMostOfIt { get; init; }

    internal static int ConcentrationOf(IEnumerable<int> counts)
    {
        var ordered = counts.OrderByDescending(c => c).ToList();
        var total = ordered.Sum();
        if (total == 0) return 0;

        var running = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            running += ordered[i];
            if (running * 5 >= total * 4) return i + 1;
        }

        return ordered.Count;
    }
}

/// <summary>
/// What a codebase uses of a package, rather than what the package offers.
///
/// The hard question about a dependency with no future is never "what are the
/// alternatives". Any blog post answers that in ten minutes. It is "which
/// alternative covers what I actually use", and nobody can answer it, because
/// it depends on code nobody has counted. A package exposes two hundred members
/// and a codebase touches six of them.
///
/// Read syntactically, like everything else here: requiring the solution to
/// compile would give up the one property that makes this usable on inherited
/// code. That has a cost, stated rather than hidden. A type is attributed to a
/// package when it appears in a file importing that package's namespace and no
/// declaration in the solution accounts for it. A file importing two candidate
/// packages cannot be split between them by syntax alone, and says so.
///
/// It over-reports, never under-reports. A type listed here may belong
/// elsewhere, and costs a reader ten seconds. One missed would make a coverage
/// figure wrong in the direction that gets a migration signed off.
/// </summary>
public class ApiSurface
{
    /// <summary>
    /// Which namespaces belong to which package.
    ///
    /// Hand-written, and the reason is the same one this project keeps
    /// returning to: a model asked for this returns the right ninety-seven and
    /// invents three, with the same confidence.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Namespaces =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // The four that hold Orchard, and most of the .NET Framework web
            // estate with it.
            ["Microsoft.AspNet.Mvc"] =
                ["System.Web.Mvc", "System.Web.Mvc.Html", "System.Web.Mvc.Ajax",
                 "System.Web.Mvc.Async", "System.Web.Mvc.Filters"],
            ["Microsoft.AspNet.Razor"] = ["System.Web.Razor"],
            ["Microsoft.AspNet.WebPages"] =
                ["System.Web.WebPages", "System.Web.Helpers", "System.Web.WebPages.Html"],
            ["Microsoft.Web.Infrastructure"] = ["Microsoft.Web.Infrastructure"],

            ["Microsoft.AspNet.WebApi.Core"] = ["System.Web.Http", "System.Web.Http.Controllers"],
            ["Microsoft.AspNet.WebApi.WebHost"] = ["System.Web.Http.WebHost"],

            // Not blockers, but the ones a migration meets next.
            ["Newtonsoft.Json"] =
                ["Newtonsoft.Json", "Newtonsoft.Json.Linq", "Newtonsoft.Json.Converters"],
            ["log4net"] = ["log4net", "log4net.Config", "log4net.Core"],
            ["Autofac"] = ["Autofac", "Autofac.Core", "Autofac.Builder"],
            ["EntityFramework"] =
                ["System.Data.Entity", "System.Data.Entity.Migrations",
                 "System.Data.Entity.Infrastructure"],
            ["NHibernate"] = ["NHibernate", "NHibernate.Cfg", "NHibernate.Criterion"],
            ["Owin"] = ["Owin", "Microsoft.Owin"],
        };

    /// <summary>
    /// Whether the framework being migrated to still supplies this name itself.
    ///
    /// Restricted to <c>System.*</c> deliberately. Everything the base library
    /// keeps, it keeps under that root, and a name that only turns up somewhere
    /// like <c>Microsoft.AspNetCore.Http</c> is a different type wearing the
    /// same word rather than the same type still being there.
    /// </summary>
    private static bool StillSupplied(string name) =>
        FrameworkTypes.ByName.TryGetValue(name, out var places)
        && places.Any(full => full.StartsWith("System.", StringComparison.Ordinal));

    /// <summary>
    /// Names a test framework supplies, which are never a package's API.
    ///
    /// `[Test]` in a file that also imports System.Web.Mvc is NUnit's attribute,
    /// and it was the tenth most used "type of Microsoft.AspNet.Mvc" on Orchard
    /// at 119 uses. Nothing in the framework reading can say so, because NUnit
    /// is not in the framework either.
    ///
    /// Hand-written and short on purpose, like the list below it. The general
    /// answer needs resolved symbols, which needs compiling, which is the
    /// property that makes this usable on code that does not build.
    /// </summary>
    private static readonly IReadOnlySet<string> TestScaffolding =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Test", "TestFixture", "TestCase", "TestCaseSource", "SetUp", "TearDown",
            "OneTimeSetUp", "OneTimeTearDown", "Ignore", "Category", "Explicit",
            "Fact", "Theory", "InlineData", "MemberData", "ClassData",
            "TestMethod", "TestClass", "TestInitialize", "TestCleanup", "DataRow",
        };

    /// <summary>
    /// Names C# supplies, which belong to no package and would drown the list.
    /// </summary>
    private static readonly IReadOnlySet<string> Builtin =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "var", "void", "object", "string", "bool", "byte", "sbyte", "char",
            "decimal", "double", "float", "int", "uint", "long", "ulong", "short",
            "ushort", "dynamic", "nint", "nuint",
            "Task", "List", "IList", "IEnumerable", "ICollection", "IReadOnlyList",
            "Dictionary", "IDictionary", "HashSet", "Exception", "Type", "Guid",
            "DateTime", "DateTimeOffset", "TimeSpan", "Nullable", "Func", "Action",
            "Attribute", "EventArgs", "Uri", "Stream", "CancellationToken",
        };

    /// <summary>The surface for every catalogued package this codebase touches.</summary>
    /// <summary>
    /// Every catalogued package this codebase uses.
    ///
    /// <paramref name="claimed"/> answers, per package, which names the
    /// catalogue records as its own. Passing null keeps the exclusion off,
    /// which is the conservative way round and a different answer: on Orchard
    /// it is 4,379 uses of `Microsoft.AspNet.Mvc` against 3,877 with the
    /// catalogue applied.
    ///
    /// So it has no default. It had one, and the command took it while the
    /// route did not, which is how the same program came to answer the same
    /// question two ways. A caller that means to abstain says so.
    /// <see cref="Surfaces"/> is what everything shipping goes through.
    /// </summary>
    public IReadOnlyList<UsageSurface> All(
        string rootPath, Func<string, IReadOnlySet<string>>? claimed)
    {
        var read = Read(rootPath);

        return Namespaces.Keys
            .Select(package => Of(read, package, claimed?.Invoke(package)))
            .Where(surface => surface.Used)
            .OrderByDescending(surface => surface.Uses)
            .ToList();
    }

    public UsageSurface Of(string rootPath, string package, IReadOnlySet<string>? claimed) =>
        Of(Read(rootPath), package, claimed);

    /// <summary>
    /// How many files import anything under each of these namespaces.
    ///
    /// A cruder question than the surface, and deliberately so. It is asked of
    /// the *modern* side of a migration, where there is no catalogue to consult
    /// because the catalogue is a list of things being left behind. So there is
    /// no abstaining decision here and no claimed set: it counts imports.
    ///
    /// Matched on the prefix, which works because a modern .NET package is
    /// named after its root namespace. `Microsoft.AspNetCore.Mvc` the package
    /// puts its types under `Microsoft.AspNetCore.Mvc` the namespace, and its
    /// family below that. The same assumption M13 makes when it asks the
    /// framework whether it carries a successor at all.
    ///
    /// One read for every prefix, because reading a tree twice to answer two
    /// questions about it is the kind of thing that turns a five second command
    /// into a minute.
    /// </summary>
    public IReadOnlyDictionary<string, int> Importing(
        string rootPath, IReadOnlyCollection<string> namespacePrefixes)
    {
        var parsed = Read(rootPath);

        return namespacePrefixes
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                prefix => prefix,
                prefix => parsed.Count(file => file.Imports.Any(
                    import => import.Equals(prefix, StringComparison.Ordinal)
                           || import.StartsWith(prefix + ".", StringComparison.Ordinal))),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// <paramref name="claimed"/> is the names the catalogue records as this
    /// package's own, and it is what keeps the exclusion below honest.
    ///
    /// Left null, nothing is excluded. That is the conservative default on
    /// purpose: without knowing which names the package claims, dropping one
    /// makes an estimate smaller than the work is, and a tool that shrinks an
    /// estimate by guessing is worse than one that leaves noise in.
    /// </summary>
    private static UsageSurface Of(
        IReadOnlyList<ParsedFile> parsed, string package, IReadOnlySet<string>? claimed = null)
    {
        if (!Namespaces.TryGetValue(package, out var namespaces))
        {
            return new UsageSurface(package, [], [], 0,
                [$"\"{package}\" is not in the catalogue, so nothing was measured for it."], []);
        }

        var wanted = namespaces.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Declared somewhere in this solution, so it belongs to the codebase
        // rather than to the package, whatever a file happens to import.
        var local = parsed.SelectMany(f => f.Declared).ToHashSet(StringComparer.Ordinal);

        var uses = new Dictionary<string, (int Uses, HashSet<string> Files)>(StringComparer.Ordinal);
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        var suppliedUses = 0;
        var perFile = new List<int>();
        var heaviest = new List<FileUse>();
        var files = 0;
        var ambiguous = 0;

        foreach (var file in parsed)
        {
            if (!file.Imports.Any(wanted.Contains)) continue;
            files++;

            // A file importing two catalogued packages cannot be split between
            // them by syntax alone. Counted so the answer can say so.
            if (Namespaces.Any(other =>
                    !other.Key.Equals(package, StringComparison.OrdinalIgnoreCase)
                    && other.Value.Any(file.Imports.Contains)))
            {
                ambiguous++;
            }

            var here = 0;

            foreach (var name in file.TypeNames)
            {
                if (Builtin.Contains(name) || TestScaffolding.Contains(name)
                    || local.Contains(name)) continue;

                // A name the framework being migrated to still supplies under
                // System.* is not this package's, whatever a file happens to
                // import beside it. Orchard's MVC files name TextWriter,
                // ArgumentException, Lazy and XElement, and counting those as
                // uses of ASP.NET MVC put 502 calls of work into an estimate
                // that had none.
                //
                // Only System.*, and never a name the catalogue records as this
                // package's own. Both halves earn their place, and the second
                // was added after measuring: `Newtonsoft.Json.JsonSerializer`
                // and `System.Text.Json.JsonSerializer` share a name, so the
                // first rule alone dropped fifteen real Newtonsoft types over
                // sixty-five uses and made that migration look smaller than it is.
                //
                // HttpContext needs neither half to stay: it resolves to
                // Microsoft.AspNetCore.Http today, which is not System.*, and it
                // is emphatically not System.Web.HttpContext.
                // `claimed` null means nobody said what this package claims, and
                // then nothing is dropped. An empty set is a different statement:
                // it says the catalogue claims none of them, which is the true
                // situation for a package it has no entry for.
                if (claimed is not null && !claimed.Contains(name) && StillSupplied(name))
                {
                    supplied.Add(name);
                    suppliedUses++;
                    continue;
                }

                if (!uses.TryGetValue(name, out var seen))
                    seen = (0, new HashSet<string>(StringComparer.Ordinal));

                seen.Files.Add(file.Path);
                uses[name] = (seen.Uses + 1, seen.Files);
                here++;
            }

            perFile.Add(here);
            if (here > 0) heaviest.Add(new FileUse(file.Path, here, file.Lines));
        }

        var types = uses
            .Select(entry => new ApiUse(entry.Key, entry.Value.Uses, entry.Value.Files.Count))
            .OrderByDescending(t => t.Uses)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var notes = new List<string>();

        if (files > 0)
        {
            notes.Add(
                "Read from the syntax, not from a compilation. A type is counted when it "
                + "appears in a type position in a file importing this package, and no "
                + "declaration in the solution accounts for it. Static calls are not "
                + "counted: telling Assert.That from services.Add needs resolved symbols.");
        }

        if (!FrameworkTypes.Readable)
        {
            notes.Add(
                "The target framework's own surface could not be read in this build, so "
                + "names it still supplies are counted here as though they were this "
                + "package's. The figure is therefore higher than the same codebase would "
                + "give from a build that can read it.");
        }

        if (supplied.Count > 0)
        {
            notes.Add(
                $"{supplied.Count} name(s) over {suppliedUses} use(s) were left out because the "
                + "target framework still supplies them under System.* and the catalogue does "
                + "not record them as this package's, so they are not its work however often "
                + "its files name them.");
        }

        if (ambiguous > 0)
        {
            notes.Add(
                $"{ambiguous} of those {files} file(s) import another catalogued package as "
                + "well, and syntax cannot say which of the two a given type came from. "
                + "Their types are counted here and there.");
        }

        // Long enough that a local model spends minutes on it and a reader
        // scrolls past it. Measured rather than chosen: Orchard's heaviest user
        // of ASP.NET MVC is 821 lines and took longer than the patience for it.
        const int TooLongToProject = 400;

        var showable = heaviest.Where(f => f.Lines <= TooLongToProject).ToList();

        if (showable.Count < heaviest.Count)
        {
            notes.Add(
                $"{heaviest.Count - showable.Count} file(s) using this package are longer "
                + $"than {TooLongToProject} lines and are left out of the list to project. "
                + "They are the same rewrite, at a length no local model finishes and nobody "
                + "reads in a browser.");
        }

        return new UsageSurface(
            package, namespaces, types, files, notes,
            // No falling back to the long ones. Offering a file the projection
            // will then refuse is worse than offering none and saying why.
            showable
                .OrderByDescending(f => f.Density)
                .ThenByDescending(f => f.Uses)
                .ThenBy(f => f.Path, StringComparer.Ordinal)
                .Take(20)
                .ToList())
        {
            FilesForMostOfIt = UsageSurface.ConcentrationOf(perFile),
        };
    }

    private sealed record ParsedFile(
        string Path,
        IReadOnlySet<string> Imports,
        IReadOnlyList<string> TypeNames,
        IReadOnlyList<string> Declared,
        int Lines);

    private static IReadOnlyList<ParsedFile> Read(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        var parsed = new List<ParsedFile>();

        foreach (var path in SourceTree.CSharpUnder(rootPath))
        {
            string source;
            try
            {
                source = File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(source).GetRoot();
            }
            catch (Exception)
            {
                continue;
            }

            parsed.Add(new ParsedFile(
                path,
                root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                    .Select(u => u.Name?.ToString())
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                NamesIn(root),
                Declarations(root),
                source.AsSpan().Count('\n') + 1));
        }

        return parsed;
    }

    /// <summary>
    /// Names this file declares, which are therefore not the package's.
    ///
    /// Delegates are included and are not a detail: BaseTypeDeclarationSyntax
    /// covers classes, structs, interfaces, enums and records, and not
    /// delegates, so Orchard's own `Localizer` was being reported as one of the
    /// most used types in ASP.NET MVC.
    ///
    /// Generic parameters too. `T` is declared by the method it sits on and
    /// belongs to nobody.
    /// </summary>
    private static List<string> Declarations(SyntaxNode root)
    {
        var declared = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t.Identifier.Text)
            .ToList();

        declared.AddRange(root.DescendantNodes().OfType<DelegateDeclarationSyntax>()
            .Select(d => d.Identifier.Text));

        declared.AddRange(root.DescendantNodes().OfType<TypeParameterSyntax>()
            .Select(p => p.Identifier.Text));

        return declared;
    }

    /// <summary>
    /// Every name that is genuinely in a type position.
    ///
    /// Not `OfType&lt;TypeSyntax&gt;()`, which is the obvious way and is wrong:
    /// in Roslyn `IdentifierNameSyntax` derives from `TypeSyntax`, so every
    /// identifier in every expression qualifies. Measured against Orchard that
    /// returned `x`, `builder`, `result` and `Count` as the most used types in
    /// the codebase, which is how the mistake was found. The unit tests all
    /// passed.
    ///
    /// So the positions are named one by one. Static calls are deliberately
    /// not among them: telling `Assert.That` from `services.Add` needs resolved
    /// symbols, and guessing from the capital letter would put half the
    /// codebase's local variables back in the list.
    /// </summary>
    private static List<string> NamesIn(SyntaxNode root)
    {
        var names = new List<string>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case SimpleBaseTypeSyntax inherited: Collect(inherited.Type, names); break;
                case ParameterSyntax { Type: { } parameter }: Collect(parameter, names); break;
                case MethodDeclarationSyntax method: Collect(method.ReturnType, names); break;
                case PropertyDeclarationSyntax property: Collect(property.Type, names); break;
                case IndexerDeclarationSyntax indexer: Collect(indexer.Type, names); break;
                case DelegateDeclarationSyntax @delegate: Collect(@delegate.ReturnType, names); break;
                case OperatorDeclarationSyntax @operator: Collect(@operator.ReturnType, names); break;
                case VariableDeclarationSyntax variable: Collect(variable.Type, names); break;
                case ObjectCreationExpressionSyntax creation: Collect(creation.Type, names); break;
                case CastExpressionSyntax cast: Collect(cast.Type, names); break;
                case TypeOfExpressionSyntax typeOf: Collect(typeOf.Type, names); break;
                case DefaultExpressionSyntax @default: Collect(@default.Type, names); break;
                case CatchDeclarationSyntax caught: Collect(caught.Type, names); break;
                case TypeConstraintSyntax constraint: Collect(constraint.Type, names); break;
                case DeclarationPatternSyntax pattern: Collect(pattern.Type, names); break;

                // `x as Controller` and `x is Controller`.
                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.AsExpression)
                      || binary.IsKind(SyntaxKind.IsExpression):
                    if (binary.Right is TypeSyntax right) Collect(right, names);
                    break;

                case AttributeSyntax attribute:
                    var attributeName = Simple(attribute.Name);
                    if (attributeName is not null)
                    {
                        // Written without the suffix, declared with it.
                        names.Add(attributeName.EndsWith("Attribute", StringComparison.Ordinal)
                            ? attributeName[..^"Attribute".Length]
                            : attributeName);
                    }
                    break;
            }
        }

        return names;
    }

    /// <summary>
    /// Adds a type and everything inside it.
    ///
    /// `IList&lt;Controller&gt;` is a use of Controller, and stopping at the
    /// outer name would miss most of what a codebase touches.
    /// </summary>
    private static void Collect(TypeSyntax type, List<string> names)
    {
        switch (type)
        {
            case PredefinedTypeSyntax:
                return;

            case GenericNameSyntax generic:
                names.Add(generic.Identifier.Text);
                foreach (var argument in generic.TypeArgumentList.Arguments)
                    Collect(argument, names);
                return;

            case QualifiedNameSyntax qualified:
                Collect(qualified.Right, names);
                return;

            case NullableTypeSyntax nullable:
                Collect(nullable.ElementType, names);
                return;

            case ArrayTypeSyntax array:
                Collect(array.ElementType, names);
                return;

            case TupleTypeSyntax tuple:
                foreach (var element in tuple.Elements) Collect(element.Type, names);
                return;

            case IdentifierNameSyntax identifier:
                names.Add(identifier.Identifier.Text);
                return;
        }
    }

    private static string? Simple(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        QualifiedNameSyntax qualified => Simple(qualified.Right),
        _ => null,
    };

}
