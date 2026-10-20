using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LegacyLens.Analysis;

public enum EntryKind
{
    MvcAction,
    WebApiAction,
    WcfOperation,
    AsmxMethod,
    HttpHandler,
    HttpModule,
    ApplicationEvent,
    OwinStartup,
    SignalRHub,
    BackgroundTask,
    WindowsService,
    ProgramMain,
    /// <summary>A remote procedure endpoint: XML-RPC handlers.</summary>
    RemoteProcedure,
    /// <summary>A command a command line dispatches by name.</summary>
    Command,
}

/// <summary>One place where a user, a client or the system enters the code.</summary>
public record EntryPoint(
    EntryKind Kind,
    string Project,
    string Type,
    string Member,
    string Path,
    int Line,
    /// <summary>What else the declaration says: an HTTP verb, the contract it implements.</summary>
    string? Detail = null);

/// <summary>
/// Where a codebase is entered: every action, operation, handler and job a
/// request or a schedule can reach, each with the file and line it is
/// declared on.
///
/// The first piece of the map. A flow is traced from an entry point inward, so
/// the list of entry points is the list of everything that can be traced, and
/// an entry point missing from it is a flow nobody will look at.
///
/// Read from syntax, like the rest: no compilation, so it works on a solution
/// that does not build. What that costs is inheritance across packages, which
/// a syntax tree cannot follow. It is followed through every type the solution
/// declares, which catches the in-house `AdminController : Controller` that a
/// name match on the class alone would miss, and it stops at the framework's
/// own names, which are the ones listed below.
/// </summary>
public class EntryPointReader
{
    /// <summary>Framework base types and interfaces, by simple name, and what deriving from them makes a type.</summary>
    private static readonly IReadOnlyDictionary<string, EntryKind> Roots = new Dictionary<string, EntryKind>(StringComparer.Ordinal)
    {
        ["Controller"] = EntryKind.MvcAction,
        ["AsyncController"] = EntryKind.MvcAction,
        ["ApiController"] = EntryKind.WebApiAction,
        ["IHttpHandler"] = EntryKind.HttpHandler,
        ["IHttpAsyncHandler"] = EntryKind.HttpHandler,
        ["HttpTaskAsyncHandler"] = EntryKind.HttpHandler,
        ["IHttpModule"] = EntryKind.HttpModule,
        ["HttpApplication"] = EntryKind.ApplicationEvent,
        ["Hub"] = EntryKind.SignalRHub,
        ["ServiceBase"] = EntryKind.WindowsService,
        // Schedulers: Quartz, Orchard's own, and the shape most in-house ones take.
        ["IJob"] = EntryKind.BackgroundTask,
        ["IBackgroundTask"] = EntryKind.BackgroundTask,
        ["IScheduledTaskHandler"] = EntryKind.BackgroundTask,
        // Remote publishing. Orchard and most .NET blog engines of its age
        // expose the MetaWeblog API this way.
        ["IXmlRpcHandler"] = EntryKind.RemoteProcedure,
    };

    private static readonly HashSet<string> HttpVerbs = new(StringComparer.Ordinal)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "AcceptVerbs",
    };

    private sealed record Method(string Name, int Line, bool Public, bool Static, bool Override, IReadOnlyList<string> Attributes);

    private sealed record Declared(
        string Name, bool Interface, bool Abstract, string Path, int Line,
        IReadOnlyList<string> Bases, IReadOnlyList<string> Attributes, IReadOnlyList<Method> Methods);

    /// <summary>
    /// Every entry point of a solution, each file attributed to the project
    /// whose folder holds it. Test projects are left out: a test calls an
    /// action, it is not one.
    /// </summary>
    public static IReadOnlyList<EntryPoint> ReadSolution(string rootPath, SolutionMap map)
    {
        var root = Path.GetFullPath(rootPath);
        var folders = map.Projects
            .Select(p => (Project: p, Folder: Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, p.Path)))))
            .Where(p => p.Folder is not null)
            .Select(p => (p.Project, Folder: p.Folder! + Path.DirectorySeparatorChar))
            .OrderByDescending(p => p.Folder.Length)
            .ToList();

        IEnumerable<(string, string, string)> Files()
        {
            foreach (var path in SourceTree.CSharpUnder(root))
            {
                var owner = folders.FirstOrDefault(f => path.StartsWith(f.Folder, StringComparison.Ordinal));
                if (owner.Project is null || owner.Project.Kind == ProjectKind.Test) continue;

                string source;
                try { source = File.ReadAllText(path); }
                catch (IOException) { continue; }

                yield return (owner.Project.Name, Path.GetRelativePath(root, path).Replace('\\', '/'), source);
            }
        }

        return new EntryPointReader().Read(Files());
    }

    /// <param name="files">Source files with the project each belongs to.</param>
    public IReadOnlyList<EntryPoint> Read(IEnumerable<(string Project, string Path, string Source)> files)
    {
        var declared = new List<(string Project, Declared Type)>();

        foreach (var (project, path, source) in files)
        {
            var root = CSharpSyntaxTree.ParseText(source, path: path).GetRoot();
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (type is not (ClassDeclarationSyntax or InterfaceDeclarationSyntax)) continue;
                declared.Add((project, Describe(type, path)));
            }
        }

        // Every name the solution declares, with the names after its colon.
        // Partial classes and same-named types in different namespaces are
        // merged: a syntax tree cannot tell them apart, and the union only
        // ever adds an ancestor, never removes one.
        var bases = declared
            .GroupBy(d => d.Type.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(d => d.Type.Bases).Distinct().ToList(), StringComparer.Ordinal);

        var contracts = declared
            .Where(d => d.Type.Interface && d.Type.Attributes.Contains("ServiceContract"))
            .GroupBy(d => d.Type.Name, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(d => d.Type.Methods).Where(m => m.Attributes.Contains("OperationContract")).Select(m => m.Name).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var entries = new List<EntryPoint>();
        foreach (var (project, type) in declared)
        {
            if (type.Interface) continue;

            var ancestors = Ancestors(type.Name, bases);
            var kinds = ancestors.Where(Roots.ContainsKey).Select(a => Roots[a]).Distinct().ToList();

            foreach (var kind in kinds)
                entries.AddRange(Of(kind, project, type));

            foreach (var contract in ancestors.Where(contracts.ContainsKey))
            {
                foreach (var method in type.Methods.Where(m => contracts[contract].Contains(m.Name)))
                    entries.Add(new(EntryKind.WcfOperation, project, type.Name, method.Name, type.Path, method.Line, contract));
            }

            foreach (var method in type.Methods.Where(m => m.Attributes.Contains("WebMethod")))
                entries.Add(new(EntryKind.AsmxMethod, project, type.Name, method.Name, type.Path, method.Line));

            // A command line that dispatches by name finds its commands by an
            // attribute carrying the name, which is the one thing they share.
            foreach (var method in type.Methods.Where(m => m.Attributes.Contains("CommandName")))
                entries.Add(new(EntryKind.Command, project, type.Name, method.Name, type.Path, method.Line));

            // OWIN finds its startup class by convention: Configuration(IAppBuilder).
            foreach (var method in type.Methods.Where(m => m.Name == "Configuration" && m.Public && !m.Static
                                                           && type.Name.EndsWith("Startup", StringComparison.Ordinal)))
                entries.Add(new(EntryKind.OwinStartup, project, type.Name, method.Name, type.Path, method.Line));

            foreach (var method in type.Methods.Where(m => m.Name == "Main" && m.Static))
                entries.Add(new(EntryKind.ProgramMain, project, type.Name, method.Name, type.Path, method.Line));
        }

        return entries
            .OrderBy(e => e.Kind).ThenBy(e => e.Project, StringComparer.Ordinal)
            .ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Line)
            .ToList();
    }

    private static IEnumerable<EntryPoint> Of(EntryKind kind, string project, Declared type)
    {
        switch (kind)
        {
            // An abstract controller is a base for others; its public methods
            // are entered through each concrete one, and listed there.
            case EntryKind.MvcAction or EntryKind.WebApiAction when !type.Abstract:
                foreach (var method in type.Methods.Where(m => m.Public && !m.Static && !m.Override
                                                              && !m.Attributes.Contains("NonAction")))
                {
                    var verbs = method.Attributes.Where(HttpVerbs.Contains).ToList();
                    yield return new(kind, project, type.Name, method.Name, type.Path, method.Line,
                        verbs.Count == 0 ? null : string.Join(", ", verbs));
                }
                break;

            case EntryKind.ApplicationEvent:
                foreach (var method in type.Methods.Where(m => m.Name.StartsWith("Application_", StringComparison.Ordinal)
                                                              || m.Name.StartsWith("Session_", StringComparison.Ordinal)))
                    yield return new(kind, project, type.Name, method.Name, type.Path, method.Line);
                break;

            case EntryKind.SignalRHub when !type.Abstract:
                foreach (var method in type.Methods.Where(m => m.Public && !m.Static && !m.Override))
                    yield return new(kind, project, type.Name, method.Name, type.Path, method.Line);
                break;

            case EntryKind.HttpHandler or EntryKind.HttpModule or EntryKind.BackgroundTask or EntryKind.WindowsService
                or EntryKind.RemoteProcedure when !type.Abstract:
                yield return new(kind, project, type.Name, "", type.Path, type.Line);
                break;
        }
    }

    private static HashSet<string> Ancestors(string name, Dictionary<string, List<string>> bases)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(bases.GetValueOrDefault(name) ?? []);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (!seen.Add(next)) continue;
            foreach (var further in bases.GetValueOrDefault(next) ?? []) pending.Push(further);
        }
        return seen;
    }

    private static Declared Describe(TypeDeclarationSyntax type, string path)
    {
        var modifiers = type.Modifiers.Select(m => m.ValueText).ToHashSet(StringComparer.Ordinal);
        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Select(m =>
            {
                var mods = m.Modifiers.Select(x => x.ValueText).ToHashSet(StringComparer.Ordinal);
                return new Method(
                    m.Identifier.ValueText,
                    Line(m.Identifier),
                    mods.Contains("public") || type is InterfaceDeclarationSyntax,
                    mods.Contains("static"),
                    mods.Contains("override"),
                    AttributeNames(m.AttributeLists));
            })
            .ToList();

        return new Declared(
            type.Identifier.ValueText,
            type is InterfaceDeclarationSyntax,
            modifiers.Contains("abstract"),
            path,
            Line(type.Identifier),
            type.BaseList?.Types.Select(t => Simple(t.Type)).ToList() ?? [],
            AttributeNames(type.AttributeLists),
            methods);
    }

    private static int Line(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>`System.Web.Mvc.Controller` and `Controller&lt;T&gt;` both as `Controller`.</summary>
    private static string Simple(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => Simple(q.Right),
        AliasQualifiedNameSyntax a => Simple(a.Name),
        GenericNameSyntax g => g.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => type.ToString(),
    };

    /// <summary>`[HttpPost]`, `[System.Web.Mvc.HttpPostAttribute]` and `[HttpPost()]` all as `HttpPost`.</summary>
    private static IReadOnlyList<string> AttributeNames(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes)
            .Select(a => Simple(a.Name))
            .Select(n => n.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^"Attribute".Length] : n)
            .ToList();
}
