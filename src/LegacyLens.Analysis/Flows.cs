using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LegacyLens.Analysis;

/// <summary>How sure a link of a flow is.</summary>
public enum Link
{
    /// <summary>One declaration in the solution answers the call.</summary>
    Resolved,
    /// <summary>The call goes through an interface with one implementation in the solution.</summary>
    ThroughInterface,
    /// <summary>Several implementations could answer; which one runs is decided at run time.</summary>
    Inferred,
    /// <summary>An interface or abstract member nothing in the solution implements.</summary>
    Unimplemented,
}

/// <summary>One call in a flow: where it is made, what answers it, and how sure that is.</summary>
public record FlowStep(
    string Call,
    string CalledAt,
    Link Link,
    /// <summary>The declarations that answer, as "Type.Member path:line"; several when inferred.</summary>
    IReadOnlyList<string> Targets,
    IReadOnlyList<FlowStep> Next)
{
    /// <summary>
    /// The tests whose code reaches a declaration answering this call, as
    /// "TestType.TestMethod path:line". Reached in the code, which is not the
    /// same as covered when they run: that takes running them.
    /// </summary>
    public IReadOnlyList<string> ReachedBy { get; init; } = [];
}

public record Flow(
    string Entry,
    string DeclaredAt,
    IReadOnlyList<FlowStep> Steps,
    /// <summary>The tests whose code reaches the entry point itself.</summary>
    IReadOnlyList<string> EntryReachedBy,
    /// <summary>Calls whose receiver is a type outside the solution: a package or the framework.</summary>
    int LeavesSolution,
    /// <summary>Calls whose receiver could not be typed from syntax alone.</summary>
    int Untyped,
    int Depth);

/// <summary>
/// A flow traced from an entry point inward, with the file and the symbol on
/// every link, and how sure each link is.
///
/// Read from syntax. A call's receiver is typed from what the solution
/// declares: a field or a constructor-injected dependency, a property, a
/// parameter, a local assigned from <c>new</c>, a static type. An interface is
/// followed to its implementation when the solution has exactly one, said to
/// be inferred when it has several, and said to have none when it has none.
/// A call into a package or the framework ends the branch and is counted, not
/// listed: a flow is about what this codebase does, and the hundred calls to
/// LINQ along the way are not.
/// </summary>
public class FlowTracer
{
    private sealed record MethodDecl(string Type, string Name, int Parameters, string? Returns, string Path, int Line,
        IReadOnlyDictionary<string, string> ParameterTypes, SyntaxNode? Body,
        int Required = 0, bool Params = false, string? Extends = null, IReadOnlyList<string?>? Ordered = null,
        int Generic = 0, bool Callable = true, bool IsTest = false)
    {
        /// <summary>Whether a call with that many arguments can bind here, optional and params arrays included.</summary>
        public bool Takes(int arguments) =>
            arguments >= Required && (Params || arguments <= Parameters);
    }

    private sealed class TypeDecl
    {
        public required string Name { get; init; }
        public bool Interface { get; init; }
        /// <summary>Declared only in test projects: a fixture, a stub or a fake.</summary>
        public bool Test { get; set; }
        public List<string> Bases { get; } = [];
        public Dictionary<string, string> Members { get; } = new(StringComparer.Ordinal);
        public List<MethodDecl> Methods { get; } = [];
    }

    private readonly Dictionary<string, TypeDecl> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _implementations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MethodDecl>> _extensions = new(StringComparer.Ordinal);

    public FlowTracer(IEnumerable<(string Path, string Source)> files)
        : this(files.Select(f => (f.Path, f.Source, false)))
    {
    }

    /// <param name="files">Source files, each marked when it belongs to a test project.</param>
    public FlowTracer(IEnumerable<(string Path, string Source, bool Test)> files)
    {
        foreach (var (path, source, test) in files)
        {
            var root = CSharpSyntaxTree.ParseText(source, path: path).GetRoot();
            foreach (var syntax in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                Add(syntax, path, test);
        }

        // Every class under every interface it reaches, through the
        // solution's own bases. A stub in a test project implements the
        // interface too, and counting it would turn every production call
        // through that interface into a guess between the real thing and the
        // fake: test types are left out.
        foreach (var type in _types.Values.Where(t => !t.Interface && !t.Test))
        foreach (var ancestor in Ancestors(type.Name))
        {
            if (_types.TryGetValue(ancestor, out var declared) && declared.Interface)
            {
                if (!_implementations.TryGetValue(ancestor, out var list)) _implementations[ancestor] = list = [];
                if (!list.Contains(type.Name)) list.Add(type.Name);
            }
        }
    }

    /// <summary>The flows of a solution, read from its non-test projects.</summary>
    public static FlowTracer ForSolution(string rootPath, SolutionMap map)
    {
        var root = Path.GetFullPath(rootPath);
        var tests = map.Projects.Where(p => p.Kind == ProjectKind.Test)
            .Select(p => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, p.Path))))
            .OfType<string>().Select(f => f + Path.DirectorySeparatorChar).ToList();

        return new FlowTracer(SourceTree.CSharpUnder(root)
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), Read(path),
                             tests.Any(t => path.StartsWith(t, StringComparison.Ordinal)))));

        static string Read(string path)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) { return ""; }
        }
    }

    /// <summary>Traces from <c>Type.Member</c>, at most <paramref name="depth"/> calls deep. Null when no such method.</summary>
    public Flow? Trace(string entry, int depth = 3)
    {
        var dot = entry.LastIndexOf('.');
        if (dot <= 0) return null;
        var start = Find(entry[..dot], entry[(dot + 1)..], arguments: null);
        if (start is null) return null;

        var counts = new int[2];
        var visited = new HashSet<string>(StringComparer.Ordinal) { Key(start) };
        var steps = Annotate(Steps(start, depth, visited, counts));
        return new Flow($"{start.Type}.{start.Name}", $"{start.Path}:{start.Line}", steps,
            ReachedBy().GetValueOrDefault(Describe(start)) ?? [], counts[0], counts[1], depth);
    }

    /// <summary>How many calls deep a test's own code is followed when asking what it reaches.</summary>
    private const int TestDepth = 4;

    private Dictionary<string, List<string>>? _reachedBy;

    /// <summary>
    /// For every declaration, the tests whose code reaches it: each test
    /// method followed inward with the same rules as a flow, keeping only the
    /// links that are certain, resolved or through an interface's one
    /// implementation. A test that calls an interface with two implementations
    /// may reach either, and is not counted as reaching both.
    /// </summary>
    private Dictionary<string, List<string>> ReachedBy()
    {
        if (_reachedBy is not null) return _reachedBy;

        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var type in _types.Values.Where(t => t.Test))
        {
            _bound = Bindings(type);
            foreach (var test in type.Methods.Where(m => m.IsTest))
            {
            var visited = new HashSet<string>(StringComparer.Ordinal) { Key(test) };
            var name = Describe(test);
            foreach (var target in Certain(Steps(test, TestDepth, visited, new int[2])).Distinct(StringComparer.Ordinal))
            {
                if (!map.TryGetValue(target, out var tests)) map[target] = tests = [];
                tests.Add(name);
            }
            }
        }

        _bound = null;
        return _reachedBy = map;

        static IEnumerable<string> Certain(IEnumerable<FlowStep> steps)
        {
            foreach (var step in steps)
            {
                if (step.Link is not (Link.Resolved or Link.ThroughInterface)) continue;
                yield return step.Targets[0];
                foreach (var further in Certain(step.Next)) yield return further;
            }
        }
    }

    /// <summary>The interface bindings of the test type being followed, null outside a test.</summary>
    private Dictionary<string, string>? _bound;

    /// <summary>
    /// What a test class's container binds each interface to, read from its
    /// registrations: Autofac's <c>RegisterType&lt;X&gt;().As&lt;I&gt;()</c>,
    /// Microsoft's <c>AddScoped&lt;I, X&gt;()</c> and its siblings, and
    /// Ninject's <c>Bind&lt;I&gt;().To&lt;X&gt;()</c>. Only to types the
    /// solution declares outside its tests: a binding to a stub reaches the stub.
    /// </summary>
    private Dictionary<string, string> Bindings(TypeDecl type)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var body in type.Methods.Select(m => m.Body).OfType<SyntaxNode>())
        foreach (var call in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } access) continue;
            var args = generic.TypeArgumentList.Arguments.Select(Simple).ToList();
            string? contract = null, concrete = null;

            switch (generic.Identifier.ValueText)
            {
                case "As" or "To" when args.Count == 1:
                    // Down the chain to the registration it qualifies.
                    var inner = access.Expression;
                    while (inner is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m })
                    {
                        if (m.Name is GenericNameSyntax { Identifier.ValueText: "RegisterType" or "Bind" } r
                            && r.TypeArgumentList.Arguments.Count == 1)
                        {
                            var named = Simple(r.TypeArgumentList.Arguments[0]);
                            (contract, concrete) = r.Identifier.ValueText == "Bind" ? (named, args[0]) : (args[0], named);
                            break;
                        }
                        inner = m.Expression;
                    }
                    break;
                case "AddScoped" or "AddTransient" or "AddSingleton" when args.Count == 2:
                    (contract, concrete) = (args[0], args[1]);
                    break;
            }

            if (contract is not null && concrete is not null
                && _types.TryGetValue(concrete, out var declared) && !declared.Test && !declared.Interface)
                bindings[contract] = concrete;
        }
        return bindings;
    }

    /// <summary>Each step with the tests that reach any declaration answering it.</summary>
    private List<FlowStep> Annotate(IEnumerable<FlowStep> steps)
    {
        var reached = ReachedBy();
        return steps.Select(step => step with
        {
            Next = Annotate(step.Next),
            ReachedBy = step.Targets.SelectMany(t => reached.GetValueOrDefault(t) ?? []).Distinct(StringComparer.Ordinal).ToList(),
        }).ToList();
    }

    private List<FlowStep> Steps(MethodDecl method, int depth, HashSet<string> visited, int[] counts)
    {
        var steps = new List<FlowStep>();
        if (method.Body is null || depth <= 0) return steps;

        var locals = Locals(method);
        foreach (var invocation in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var (receiverType, name) = Target(invocation, method, locals);
            if (name is null) continue;
            // The line of the name called, which is where a reader looks: in a
            // chain spread over lines, the expression starts lines earlier.
            var named = invocation.Expression is MemberAccessExpressionSyntax access ? access.Name : invocation.Expression;
            var at = $"{method.Path}:{named.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
            var arguments = invocation.ArgumentList.Arguments.Count;

            if (receiverType is null) { counts[1]++; continue; }
            if (!_types.TryGetValue(receiverType, out var type))
            {
                // A type from outside, extended inside: `IEnumerable<T>.ToPagedList()`.
                var outside = Extension(receiverType, name, arguments, Generic(invocation));
                if (outside is null) { counts[0]++; continue; }
                steps.Add(Step($"{receiverType}.{name}", at, Link.Resolved, [outside], depth, visited, counts));
                continue;
            }

            var call = $"{receiverType}.{name}";
            var direct = Overloads(receiverType, name, invocation, method, locals);
            if (direct.Count == 0 && Extension(receiverType, name, arguments, Generic(invocation)) is { } extension)
            {
                steps.Add(Step(call, at, Link.Resolved, [extension], depth, visited, counts));
                continue;
            }

            if (!type.Interface)
            {
                steps.Add(direct.Count switch
                {
                    0 => Count(counts), // inherited from outside the solution
                    1 => Step(call, at, Link.Resolved, direct, depth, visited, counts),
                    _ => new FlowStep(call, at, Link.Inferred, direct.Select(Describe).ToList(), []),
                });
                continue;
            }

            // Under a test whose container binds the interface, the binding
            // answers, as it does when the test runs.
            var candidates = _bound is not null && _bound.TryGetValue(receiverType, out var bound)
                ? [bound]
                : _implementations.GetValueOrDefault(receiverType) ?? [];
            var implementations = candidates
                .SelectMany(i => Overloads(i, name, invocation, method, locals, throughInterface: true))
                .DistinctBy(Key)
                .ToList();

            steps.Add(implementations.Count switch
            {
                0 => new FlowStep(call, at, Link.Unimplemented, [], []),
                1 => Step(call, at, Link.ThroughInterface, implementations, depth, visited, counts),
                _ => new FlowStep(call, at, Link.Inferred, implementations.Select(Describe).ToList(), []),
            });
        }

        steps.RemoveAll(step => step is null);
        return steps;
    }

    /// <summary>How many type arguments a call spells out: `Get&lt;BlogPart&gt;()` has one.</summary>
    private static int Generic(InvocationExpressionSyntax call) => call.Expression switch
    {
        GenericNameSyntax g => g.TypeArgumentList.Arguments.Count,
        MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g.TypeArgumentList.Arguments.Count,
        _ => 0,
    };

    /// <summary>Counts a call that leaves the solution, and adds nothing to the flow.</summary>
    private static FlowStep Count(int[] counts)
    {
        counts[0]++;
        return null!;
    }

    private FlowStep Step(string call, string at, Link link, List<MethodDecl> targets, int depth, HashSet<string> visited, int[] counts)
    {
        var target = targets[0];
        // A method already on the path is named and not entered again.
        var next = visited.Add(Key(target)) ? Steps(target, depth - 1, visited, counts) : [];
        return new FlowStep(call, at, link, [Describe(target)], next);
    }

    private static string Key(MethodDecl m) => $"{m.Type}.{m.Name}/{m.Parameters}@{m.Path}:{m.Line}";

    private static string Describe(MethodDecl m) => $"{m.Type}.{m.Name} {m.Path}:{m.Line}";

    /// <summary>The type a call is made on, by name, and the member called.</summary>
    private (string? Type, string? Name) Target(InvocationExpressionSyntax invocation, MethodDecl method, Dictionary<string, string> locals) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax id => (Find(method.Type, id.Identifier.ValueText, null) is null ? null : method.Type, id.Identifier.ValueText),
            GenericNameSyntax g => (Find(method.Type, g.Identifier.ValueText, null) is null ? null : method.Type, g.Identifier.ValueText),
            MemberAccessExpressionSyntax access => (TypeOf(access.Expression, method, locals), access.Name.Identifier.ValueText),
            _ => (null, null),
        };

    /// <summary>The declared type of an expression, as far as syntax and the solution's declarations say.</summary>
    private string? TypeOf(ExpressionSyntax expression, MethodDecl method, Dictionary<string, string> locals)
    {
        switch (expression)
        {
            case ThisExpressionSyntax or BaseExpressionSyntax:
                return method.Type;

            case IdentifierNameSyntax id:
            {
                var name = id.Identifier.ValueText;
                if (locals.TryGetValue(name, out var local)) return local;
                if (method.ParameterTypes.TryGetValue(name, out var parameter)) return parameter;
                var member = Member(method.Type, name);
                if (member is not null) return member;
                // A static call on a type: `SomeHelper.Do()`.
                return _types.ContainsKey(name) ? name : null;
            }

            case MemberAccessExpressionSyntax access:
            {
                var owner = TypeOf(access.Expression, method, locals);
                return owner is null ? null : Member(owner, access.Name.Identifier.ValueText);
            }

            case InvocationExpressionSyntax call:
            {
                var (owner, name) = Target(call, method, locals);
                if (owner is null || name is null) return null;
                var target = Find(owner, name, call.ArgumentList.Arguments.Count)
                          ?? (_implementations.GetValueOrDefault(owner) ?? [])
                                .Select(i => Find(i, name, call.ArgumentList.Arguments.Count)).FirstOrDefault(m => m is not null);
                return target?.Returns;
            }

            case ObjectCreationExpressionSyntax creation:
                return Simple(creation.Type);

            case ParenthesizedExpressionSyntax p:
                return TypeOf(p.Expression, method, locals);

            case CastExpressionSyntax cast:
                return Simple(cast.Type);

            default:
                return null;
        }
    }

    /// <summary>Locals declared with a type, or with <c>var</c> and a <c>new</c>.</summary>
    private static Dictionary<string, string> Locals(MethodDecl method)
    {
        var locals = new Dictionary<string, string>(StringComparer.Ordinal);
        if (method.Body is null) return locals;

        foreach (var declaration in method.Body.DescendantNodes().OfType<VariableDeclarationSyntax>())
        foreach (var variable in declaration.Variables)
        {
            var type = declaration.Type.IsVar
                ? variable.Initializer?.Value is ObjectCreationExpressionSyntax created ? Simple(created.Type) : null
                : Simple(declaration.Type);
            if (type is not null) locals[variable.Identifier.ValueText] = type;
        }

        return locals;
    }

    /// <summary>A field's or property's type on a type or the solution's ancestors of it.</summary>
    private string? Member(string type, string name)
    {
        foreach (var candidate in Ancestors(type).Prepend(type))
        {
            if (_types.TryGetValue(candidate, out var declared) && declared.Members.TryGetValue(name, out var found))
                return found;
        }
        return null;
    }

    /// <summary>A method on a type or its solution ancestors, the overload with that many arguments preferred.</summary>
    private MethodDecl? Find(string type, string name, int? arguments)
    {
        foreach (var candidate in Ancestors(type).Prepend(type))
        {
            if (!_types.TryGetValue(candidate, out var declared)) continue;
            var named = declared.Methods.Where(m => m.Name == name && m.Extends is null).ToList();
            if (named.Count == 0) continue;
            if (arguments is null) return named[0];
            // The overload the arguments fit. None fits: the call binds to
            // something else, an extension or an inherited member outside the
            // solution, and naming this one would be a wrong link.
            return named.FirstOrDefault(m => m.Parameters == arguments) ?? named.FirstOrDefault(m => m.Takes(arguments.Value));
        }
        return null;
    }

    /// <summary>
    /// Every overload of a method on a type or its solution ancestors that a
    /// call can bind to, narrowed by the types of the arguments where syntax
    /// gives them: a literal, a <c>new</c>, a typed local, field or parameter.
    /// More than one left means the call is ambiguous from here, and is said to be.
    /// </summary>
    private List<MethodDecl> Overloads(string type, string name, InvocationExpressionSyntax call, MethodDecl caller, Dictionary<string, string> locals,
        bool throughInterface = false)
    {
        var arguments = call.ArgumentList.Arguments;
        var generic = Generic(call);
        foreach (var candidate in Ancestors(type).Prepend(type))
        {
            if (!_types.TryGetValue(candidate, out var declared)) continue;
            // Looking for what implements an interface, the interface's own
            // declaration is not an answer.
            if (throughInterface && declared.Interface) continue;
            var fitting = declared.Methods
                .Where(m => m.Name == name && m.Extends is null && m.Takes(arguments.Count)
                            && (generic == 0 || m.Generic == generic)
                            && (!throughInterface || m.Callable))
                .ToList();
            if (fitting.Count <= 1) { if (fitting.Count == 1) return fitting; continue; }

            var typed = arguments.Select(a => ArgumentType(a.Expression, caller, locals)).ToList();
            var kept = fitting.Where(m => m.Ordered is null || typed.Select((t, i) =>
                    t is null || i >= m.Ordered.Count || m.Ordered[i] is null || m.Ordered[i] == t
                    || m.Ordered[i] == "object" || Ancestors(t).Contains(m.Ordered[i]!)).All(ok => ok))
                .ToList();
            return kept.Count == 0 ? fitting : kept;
        }
        return [];
    }

    private string? ArgumentType(ExpressionSyntax expression, MethodDecl caller, Dictionary<string, string> locals) => expression switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => "string",
        InterpolatedStringExpressionSyntax => "string",
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.NumericLiteralExpression) => l.Token.Value is int ? "int" : null,
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.TrueLiteralExpression) || l.IsKind(SyntaxKind.FalseLiteralExpression) => "bool",
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression)
            && (ArgumentType(b.Left, caller, locals) == "string" || ArgumentType(b.Right, caller, locals) == "string") => "string",
        _ => TypeOf(expression, caller, locals),
    };

    /// <summary>An extension method of the solution's on a type or one of its ancestors.</summary>
    private MethodDecl? Extension(string type, string name, int arguments, int generic = 0)
    {
        var receivers = Ancestors(type).Prepend(type).ToHashSet(StringComparer.Ordinal);
        return _extensions.GetValueOrDefault(name)?
            .FirstOrDefault(m => m.Extends is not null && receivers.Contains(m.Extends) && m.Takes(arguments + 1)
                                 && (generic == 0 || m.Generic == generic));
    }

    private List<string> Ancestors(string type)
    {
        var seen = new List<string>();
        var pending = new Stack<string>(_types.GetValueOrDefault(type)?.Bases ?? []);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (seen.Contains(next) || next == type) continue;
            seen.Add(next);
            foreach (var further in _types.GetValueOrDefault(next)?.Bases ?? []) pending.Push(further);
        }
        return seen;
    }

    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod",
    };

    private void Add(TypeDeclarationSyntax syntax, string path, bool test = false)
    {
        if (syntax is not (ClassDeclarationSyntax or InterfaceDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax))
            return;

        var name = syntax.Identifier.ValueText;
        if (!_types.TryGetValue(name, out var type))
            _types[name] = type = new TypeDecl { Name = name, Interface = syntax is InterfaceDeclarationSyntax, Test = test };
        else
            type.Test &= test;

        type.Bases.AddRange(syntax.BaseList?.Types.Select(t => Simple(t.Type)).OfType<string>() ?? []);

        foreach (var member in syntax.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field when (Simple(field.Declaration.Type) ?? Keyword(field.Declaration.Type)) is { } fieldType:
                    foreach (var v in field.Declaration.Variables) type.Members[v.Identifier.ValueText] = fieldType;
                    break;
                case PropertyDeclarationSyntax property when (Simple(property.Type) ?? Keyword(property.Type)) is { } propertyType:
                    type.Members[property.Identifier.ValueText] = propertyType;
                    break;
                case MethodDeclarationSyntax method:
                    var parameters = method.ParameterList.Parameters;
                    var first = parameters.FirstOrDefault();
                    var extends = first is not null && first.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)) && first.Type is not null
                        ? Simple(first.Type) : null;
                    var declaration = new MethodDecl(
                        name,
                        method.Identifier.ValueText,
                        method.ParameterList.Parameters.Count,
                        Simple(method.ReturnType),
                        path,
                        method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        method.ParameterList.Parameters
                            .Where(p => p.Type is not null && Simple(p.Type) is not null)
                            .GroupBy(p => p.Identifier.ValueText)
                            .ToDictionary(g => g.Key, g => Simple(g.First().Type!)!, StringComparer.Ordinal),
                        (SyntaxNode?)method.Body ?? method.ExpressionBody,
                        parameters.Count(p => p.Default is null && !p.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword))),
                        parameters.Any(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword))),
                        extends,
                        parameters.Select(p => p.Type is null ? null : Simple(p.Type) ?? Keyword(p.Type)).ToList(),
                        method.TypeParameterList?.Parameters.Count ?? 0,
                        // What a caller through an interface can land on: a
                        // public method, or an explicit implementation.
                        syntax is InterfaceDeclarationSyntax
                            || method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                            || method.ExplicitInterfaceSpecifier is not null,
                        test && method.AttributeLists.SelectMany(l => l.Attributes)
                            .Select(a => a.Name.ToString().Split('.')[^1])
                            .Select(n => n.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^9] : n)
                            .Any(TestAttributes.Contains));
                    type.Methods.Add(declaration);
                    if (extends is not null)
                    {
                        if (!_extensions.TryGetValue(declaration.Name, out var list)) _extensions[declaration.Name] = list = [];
                        list.Add(declaration);
                    }
                    break;
            }
        }
    }

    /// <summary>`string`, `int` and the other keywords, by their keyword.</summary>
    private static string? Keyword(TypeSyntax type) =>
        type is PredefinedTypeSyntax predefined ? predefined.Keyword.ValueText : null;

    /// <summary>`Orchard.IFoo` and `IFoo&lt;T&gt;` as `IFoo`; `var`, arrays and tuples as nothing.</summary>
    private static string? Simple(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => Simple(q.Right),
        AliasQualifiedNameSyntax a => Simple(a.Name),
        GenericNameSyntax g => g.Identifier.ValueText,
        IdentifierNameSyntax i when !i.IsVar => i.Identifier.ValueText,
        NullableTypeSyntax n => Simple(n.ElementType),
        _ => null,
    };
}

/// <summary>A flow as an indented list: one line per call, its link, where it is made and what answers it.</summary>
public static class FlowWriter
{
    public static string Write(Flow flow)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"## {flow.Entry}");
        text.AppendLine();
        var links = Flatten(flow.Steps).ToList();
        var reached = links.Count(s => s.ReachedBy.Count > 0);
        text.AppendLine($"Declared at `{flow.DeclaredAt}`. Traced {flow.Depth} calls deep, from syntax. "
                      + (flow.EntryReachedBy.Count > 0
                          ? $"The entry point itself is reached by {Tests(flow.EntryReachedBy)}."
                          : "No test reaches the entry point itself."));
        text.AppendLine();
        text.AppendLine($"**{reached} of {links.Count} links are reached by a test**, in the code: a test's own calls lead "
                      + "there. That is evidence a test exercises it, not proof it is covered when the tests run, "
                      + "which takes running them.");
        text.AppendLine();
        Steps(text, flow.Steps, 0);
        text.AppendLine();
        text.AppendLine(
            $"Left out: {flow.LeavesSolution:N0} calls into packages or the framework, which end a branch, "
          + $"and {flow.Untyped:N0} whose receiver syntax alone could not type.");
        text.AppendLine();
        text.AppendLine("Resolved: one declaration answers. Through interface: the only implementation in the solution. "
                      + "Inferred: several could, and which runs is decided at run time. Unimplemented: nothing in the solution implements it.");
        return text.ToString();
    }

    private static IEnumerable<FlowStep> Flatten(IEnumerable<FlowStep> steps) =>
        steps.SelectMany(s => Flatten(s.Next).Prepend(s));

    /// <summary>"`A.Test1`" or "`A.Test1` and 4 other tests".</summary>
    private static string Tests(IReadOnlyList<string> tests)
    {
        var first = $"`{tests[0].Split(' ')[0]}`";
        return tests.Count == 1 ? first : $"{first} and {tests.Count - 1} other {(tests.Count == 2 ? "test" : "tests")}";
    }

    private static void Steps(System.Text.StringBuilder text, IReadOnlyList<FlowStep> steps, int indent)
    {
        foreach (var step in steps)
        {
            var pad = new string(' ', indent * 2);
            var label = step.Link switch
            {
                Link.Resolved => "resolved",
                Link.ThroughInterface => "through interface",
                Link.Inferred => $"inferred, {step.Targets.Count} candidates",
                _ => "unimplemented",
            };
            var targets = step.Targets.Count == 0 ? "nothing in the solution" : string.Join("; ", step.Targets.Select(t => $"`{t}`"));
            var tests = step.ReachedBy.Count == 0 ? "no test reaches it" : $"reached by {Tests(step.ReachedBy)}";
            text.AppendLine($"{pad}- `{step.Call}` at `{step.CalledAt}`, {label}: {targets}. {tests}");
            Steps(text, step.Next, indent + 1);
        }
    }
}
