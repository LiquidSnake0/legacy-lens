using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.Metadata;

namespace LegacyLens.Analysis;

/// <summary>One assembly found in the folder, as its metadata describes it.</summary>
public record BinaryAssembly(
    string Name,
    string Version,
    /// <summary>As ILSpy detects it, for example ".NETFramework,Version=v4.8". Empty when nothing says.</summary>
    string Framework,
    string Path,
    bool Executable,
    bool StrongNamed,
    int Types,
    IReadOnlyList<string> References)
{
    /// <summary>"AnyCPU", "x86 only", "x64 only": what the PE header and the CLI flags say it can run as.</summary>
    public string Platform { get; init; } = "AnyCPU";

    /// <summary>Native functions it calls through P/Invoke, as "library!function".</summary>
    public IReadOnlyList<string> NativeCalls { get; init; } = [];

    /// <summary>Types and methods whose names are not names a compiler writes: the mark of an obfuscator.</summary>
    public int Obfuscated { get; init; }

    public int Members { get; init; }
}

/// <summary>A part of the .NET Framework that modern .NET does not have, and who reaches for it.</summary>
public record FrameworkGap(string Assembly, string Why, IReadOnlyList<string> UsedBy, int Members);

/// <summary>Everything measured about a folder of assemblies whose source is not there.</summary>
public record BinaryAssessment(
    string Name,
    string Root,
    /// <summary>The code under assessment: assemblies without a strong name that are not a known package.</summary>
    IReadOnlyList<BinaryAssembly> Yours,
    /// <summary>Signed assemblies and known packages, shipped alongside.</summary>
    IReadOnlyList<BinaryAssembly> ThirdParty,
    /// <summary>Files that are not .NET assemblies: native libraries, or unreadable.</summary>
    IReadOnlyList<string> NotManaged,
    IReadOnlyList<FrameworkGap> Gaps,
    IReadOnlyList<Dependency> Uses,
    long ElapsedMs,
    /// <summary>The name prefixes the caller said are theirs; empty when the default rule decided.</summary>
    IReadOnlyList<string>? Prefixes = null);

/// <summary>
/// The assessment of a codebase whose source is gone, read from its
/// assemblies with ILSpy's engine.
///
/// Nothing is decompiled and nothing is run. The metadata of a compiled
/// assembly already says what the source no longer can: which assemblies it
/// references, which types of each, and which members of those types. That is
/// more exact than reading source, not less: a type is attributed to the
/// assembly the compiler resolved it to, where the source reading has to guess
/// from the namespaces a file imports.
///
/// What it cannot say is said in the report: no complexity, no change history,
/// no tests, and a member referenced once and called a thousand times counts
/// once, because the metadata records members and not call sites.
/// </summary>
public class AssemblyReader
{
    /// <summary>
    /// The parts of the .NET Framework with no counterpart on modern .NET.
    ///
    /// Hand-written and short, like the catalogue of packages: each line is a
    /// fact about the framework, and a model asked for this list would supply
    /// a plausible extra one.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Gone =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.Web"] = "ASP.NET on System.Web. ASP.NET Core does not have it and never will.",
            ["System.Web.Extensions"] = "ASP.NET AJAX and its JSON serializer, built on System.Web.",
            ["System.Web.Services"] = "ASMX web services. Modern .NET has no server side for them.",
            ["System.ServiceModel.Activation"] = "WCF services hosted in IIS. Only CoreWCF, a separate project, serves WCF on modern .NET.",
            ["System.Activities"] = "Windows Workflow Foundation 4. No successor in modern .NET.",
            ["System.Workflow.Runtime"] = "Windows Workflow Foundation 3. No successor in modern .NET.",
            ["System.Workflow.Activities"] = "Windows Workflow Foundation 3. No successor in modern .NET.",
            ["System.EnterpriseServices"] = "COM+ serviced components. Not in modern .NET.",
            ["System.Runtime.Remoting"] = ".NET Remoting. Removed from modern .NET.",
        };

    private static readonly IReadOnlyDictionary<string, string> NamespaceToPackage =
        ApiSurface.Namespaces
            .SelectMany(entry => entry.Value.Select(ns => (ns, entry.Key)))
            .GroupBy(pair => pair.ns, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

    private readonly Surfaces _surfaces = new();

    /// <param name="yours">
    /// Name prefixes of the assemblies under assessment, for example the
    /// company's own root namespace. Empty, and the rule in <see cref="IsYours(BinaryAssembly)"/> decides.
    /// </param>
    public BinaryAssessment Assess(string rootPath, IReadOnlyList<string>? yours = null)
    {
        yours ??= [];
        bool Yours(BinaryAssembly assembly) => yours.Count > 0
            ? yours.Any(prefix => assembly.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            : IsYours(assembly);

        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        var started = Stopwatch.StartNew();
        var root = System.IO.Path.GetFullPath(rootPath);
        var assemblies = new List<(BinaryAssembly Assembly, PEFile File)>();
        var notManaged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Candidates(root).OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal))
        {
            var file = Open(path);
            var relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
            if (file is null)
            {
                notManaged.Add(relative);
                continue;
            }

            // The same assembly copied into several bin folders is one assembly.
            if (!seen.Add(file.Name))
            {
                file.Dispose();
                continue;
            }

            assemblies.Add((Describe(file, relative), file));
        }

        try
        {
            var mine = assemblies.Where(a => Yours(a.Assembly)).ToList();
            var third = assemblies.Where(a => !Yours(a.Assembly)).Select(a => a.Assembly).ToList();

            return new BinaryAssessment(
                System.IO.Path.GetFileName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar)),
                root,
                mine.Select(a => a.Assembly).OrderBy(a => a.Name, StringComparer.Ordinal).ToList(),
                third.OrderBy(a => a.Name, StringComparer.Ordinal).ToList(),
                notManaged,
                Gaps(mine),
                Uses(mine),
                started.ElapsedMilliseconds,
                yours);
        }
        finally
        {
            foreach (var (_, file) in assemblies) file.Dispose();
        }
    }

    /// <summary>
    /// Yours unless it is signed or is a package this tool knows. An
    /// application's own assemblies are rarely strong-named, and every
    /// Microsoft and most third-party ones are: the rule is stated in the
    /// report, because it is a rule and not a measurement.
    /// </summary>
    public static bool IsYours(BinaryAssembly assembly) =>
        !assembly.StrongNamed
        // A known package, by its own name (EntityFramework.dll) or by the
        // namespace it is named after (System.Web.Mvc.dll).
        && !ApiSurface.Namespaces.ContainsKey(assembly.Name)
        && !NamespaceToPackage.ContainsKey(assembly.Name)
        && !assembly.Name.StartsWith("System.", StringComparison.Ordinal)
        && assembly.Name != "System"
        && !assembly.Name.StartsWith("Microsoft.", StringComparison.Ordinal);

    private static IEnumerable<string> Candidates(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (System.IO.Path.GetFileName(entry) is not (".git" or "node_modules")) pending.Push(entry);
                }
                else if (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                         || entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }
        }
    }

    private static PEFile? Open(string path)
    {
        try
        {
            var file = new PEFile(path, PEStreamOptions.PrefetchEntireImage);
            if (file.IsAssembly) return file;
            file.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is BadImageFormatException or MetadataFileNotSupportedException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static BinaryAssembly Describe(PEFile file, string relative)
    {
        var metadata = file.Metadata;
        var definition = metadata.GetAssemblyDefinition();
        var references = metadata.AssemblyReferences
            .Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        string framework;
        try
        {
            framework = file.DetectTargetFrameworkId() ?? "";
        }
        catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException)
        {
            framework = "";
        }

        var types = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
            .Where(t => metadata.GetString(t.Name) != "<Module>")
            .ToList();
        var names = types.Select(t => metadata.GetString(t.Name))
            .Concat(metadata.MethodDefinitions.Select(h => metadata.GetString(metadata.GetMethodDefinition(h).Name)))
            .ToList();

        return new BinaryAssembly(
            file.Name,
            definition.Version.ToString(),
            framework,
            relative,
            file.Reader.PEHeaders.IsExe,
            !definition.PublicKey.IsNil && metadata.GetBlobReader(definition.PublicKey).Length > 0,
            types.Count,
            references)
        {
            Platform = Platform(file.Reader.PEHeaders),
            NativeCalls = NativeCalls(metadata),
            Obfuscated = names.Count(Mangled),
            Members = names.Count,
        };
    }

    /// <summary>
    /// What the headers allow it to run as. An IL-only assembly is AnyCPU
    /// unless its CLI flags demand 32 bits or its machine is fixed to x64; a
    /// mixed-mode one carries native code for the machine it names.
    /// </summary>
    private static string Platform(PEHeaders headers)
    {
        var flags = headers.CorHeader?.Flags ?? 0;
        var machine = headers.CoffHeader.Machine;
        if (machine == Machine.Amd64) return "x64 only";
        if (machine == Machine.Arm64) return "ARM64 only";
        if ((flags & CorFlags.ILOnly) == 0) return "x86 only (mixed-mode)";
        // 32BITREQUIRED with 32BITPREFERRED is AnyCPU that prefers 32 bits
        // where it can choose, not an assembly that cannot run as 64.
        if ((flags & CorFlags.Requires32Bit) == 0) return "AnyCPU";
        return (flags & CorFlags.Prefers32Bit) != 0 ? "AnyCPU (prefers 32-bit)" : "x86 only";
    }

    private static List<string> NativeCalls(MetadataReader metadata)
    {
        var calls = new List<string>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var import = metadata.GetMethodDefinition(handle).GetImport();
            if (import.Module.IsNil) continue;
            var library = metadata.GetString(metadata.GetModuleReference(import.Module).Name);
            calls.Add($"{library}!{metadata.GetString(import.Name)}");
        }
        return calls.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// A name no C# compiler writes for something it declares: one that
    /// starts with a mark an obfuscator uses or holds characters an
    /// identifier cannot. Compiler-generated names (&lt;Main&gt;$, lambdas,
    /// <c>.ctor</c>) are not counted.
    /// </summary>
    private static bool Mangled(string name)
    {
        if (name.Length == 0 || name.StartsWith('<') || name.StartsWith('.')) return false;
        // The compiler's own: the fixed-size buffers behind an array initialiser.
        if (name.StartsWith("__StaticArrayInitTypeSize=", StringComparison.Ordinal)) return false;
        if (name[0] is '#' or '\u0001' || name.Contains('=') || name.Contains('$')) return true;

        // An explicit interface implementation is named after the interface,
        // System.IDisposable.Dispose, and every iterator has several: a
        // dotted run of identifiers, generic arguments included, is ordinary.
        var plain = name;
        for (var before = ""; before != plain;)
        {
            before = plain;
            plain = System.Text.RegularExpressions.Regex.Replace(plain, "<[^<>]*>", "");
        }
        return !plain.Split('.').All(segment =>
            segment.Length > 0 && segment.All(c => char.IsLetterOrDigit(c) || c is '_' or '`'));
    }

    /// <summary>A type reference's namespace, name and the assembly it resolves to.</summary>
    private static (string Namespace, string Name, string? Assembly) Read(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var reference = metadata.GetTypeReference(handle);
        var name = metadata.GetString(reference.Name);
        var ns = metadata.GetString(reference.Namespace);
        var scope = reference.ResolutionScope;

        // A nested type takes its namespace and its assembly from the type it
        // is nested in.
        while (scope.Kind == HandleKind.TypeReference)
        {
            var outer = metadata.GetTypeReference((TypeReferenceHandle)scope);
            ns = metadata.GetString(outer.Namespace);
            scope = outer.ResolutionScope;
        }

        var assembly = scope.Kind == HandleKind.AssemblyReference
            ? metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : null;

        var tick = name.IndexOf('`');
        return (ns, tick < 0 ? name : name[..tick], assembly);
    }

    /// <summary>Distinct members referenced through each type reference, at least one for the type itself.</summary>
    private static Dictionary<TypeReferenceHandle, int> MembersPerType(MetadataReader metadata)
    {
        var counts = metadata.TypeReferences.ToDictionary(h => h, _ => 0);
        foreach (var handle in metadata.MemberReferences)
        {
            var parent = metadata.GetMemberReference(handle).Parent;
            if (parent.Kind == HandleKind.TypeReference) counts[(TypeReferenceHandle)parent]++;
            // DbSet<Order>.Add: the parent is the instantiation, whose blob
            // names the generic type it instantiates.
            else if (parent.Kind == HandleKind.TypeSpecification && Instantiated(metadata, (TypeSpecificationHandle)parent) is { } generic)
                counts[generic]++;
        }

        foreach (var key in counts.Keys.ToList()) counts[key] = Math.Max(1, counts[key]);
        return counts;
    }

    /// <summary>The generic type reference a type specification instantiates, if that is what it is.</summary>
    private static TypeReferenceHandle? Instantiated(MetadataReader metadata, TypeSpecificationHandle handle)
    {
        try
        {
            var blob = metadata.GetBlobReader(metadata.GetTypeSpecification(handle).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
            blob.ReadSignatureTypeCode(); // class or value type
            var generic = blob.ReadTypeHandle();
            return generic.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)generic : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static List<FrameworkGap> Gaps(List<(BinaryAssembly Assembly, PEFile File)> yours)
    {
        var gaps = new Dictionary<string, (List<string> UsedBy, int Members)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (assembly, file) in yours)
        {
            var metadata = file.Metadata;
            var members = MembersPerType(metadata);
            foreach (var (handle, count) in members)
            {
                var (_, _, target) = Read(metadata, handle);
                if (target is null || !Gone.ContainsKey(target)) continue;

                if (!gaps.TryGetValue(target, out var gap)) gaps[target] = gap = ([], 0);
                if (!gap.UsedBy.Contains(assembly.Name)) gap.UsedBy.Add(assembly.Name);
                gaps[target] = (gap.UsedBy, gap.Members + count);
            }
        }

        return gaps
            .Select(g => new FrameworkGap(g.Key, Gone[g.Key], g.Value.UsedBy.Order(StringComparer.Ordinal).ToList(), g.Value.Members))
            .OrderByDescending(g => g.Members)
            .ToList();
    }

    private List<Dependency> Uses(List<(BinaryAssembly Assembly, PEFile File)> yours)
    {
        // package -> type -> (members, assemblies)
        var perPackage = new Dictionary<string, Dictionary<string, (int Members, HashSet<string> In)>>(StringComparer.OrdinalIgnoreCase);
        var perAssembly = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (assembly, file) in yours)
        {
            var metadata = file.Metadata;
            foreach (var (handle, count) in MembersPerType(metadata))
            {
                var (ns, name, _) = Read(metadata, handle);
                if (!NamespaceToPackage.TryGetValue(ns, out var package)) continue;

                var types = perPackage.TryGetValue(package, out var found) ? found : perPackage[package] = new(StringComparer.Ordinal);
                var entry = types.TryGetValue(name, out var existing) ? existing : (Members: 0, In: new HashSet<string>(StringComparer.Ordinal));
                entry.In.Add(assembly.Name);
                types[name] = (entry.Members + count, entry.In);

                var byAssembly = perAssembly.TryGetValue(package, out var a) ? a : perAssembly[package] = new(StringComparer.OrdinalIgnoreCase);
                byAssembly[assembly.Name] = byAssembly.GetValueOrDefault(assembly.Name) + count;
            }
        }

        return perPackage
            .Select(p =>
            {
                var types = p.Value
                    .Select(t => new ApiUse(t.Key, t.Value.Members, t.Value.In.Count))
                    .OrderByDescending(t => t.Uses).ThenBy(t => t.Name, StringComparer.Ordinal)
                    .ToList();
                var surface = new UsageSurface(p.Key, ApiSurface.Namespaces[p.Key], types,
                    perAssembly[p.Key].Count, [], [])
                {
                    FilesForMostOfIt = UsageSurface.ConcentrationOf(perAssembly[p.Key].Values),
                };
                return new Dependency(surface, _surfaces.Candidates(surface));
            })
            .OrderByDescending(d => d.Surface.Uses)
            .ToList();
    }
}
