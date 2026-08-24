using System.Text.Json;
using LegacyLens.Analysis;
using LegacyLens.Api;
using LegacyLens.Api.Embeddings;
using LegacyLens.Api.Generation;
using LegacyLens.Api.Ingestion;
using LegacyLens.Api.Storage;

// Writing the report is the one capability here that needs no server, no model
// and no index: it reads a directory and prints markdown. Exposing it as an
// argument as well as an endpoint is what lets a build regenerate the document
// on every commit without standing a service up first.
if (args is ["report", var target, ..])
{
    Console.Out.Write(new ReportWriter().Write(new Assessor().Assess(target)));
    return;
}

// Characterization: record what a compiled assembly does, as tests known to
// pass. A command and never a route, because it loads someone's assembly and
// calls into it. Writing the files takes a second argument, so that pointing it
// at something to see what it would produce cannot leave anything behind.
if (args is ["characterize", var assemblyPath, ..])
{
    Characterize.Run(assemblyPath, args.Skip(2).ToArray());
    return;
}

// The mechanical conversions, as a patch. A command as well as a route,
// because what comes out is meant to be redirected to a file and handed to
// `git apply` by a person who read it first. Nothing here writes to the tree.
if (args is ["convert", var convertTarget, ..])
{
    Conversions.Run(convertTarget, args.Skip(2).ToArray());
    return;
}

// What a codebase uses of its dependencies, rather than what they offer.
//
// The first question anyone asks about a package with no future is which
// alternative covers what they actually use, and nobody can answer it without
// counting. No model is involved: this reads the syntax.
if (args is ["surface", var surfaceTarget, ..])
{
    var surfaces = args.Length > 2
        ? [new ApiSurface().Of(surfaceTarget, args[2])]
        : new ApiSurface().All(surfaceTarget);

    foreach (var surface in surfaces)
    {
        Console.Out.WriteLine(
            $"{surface.Package}: {surface.Uses} use(s) of {surface.Types.Count} type(s), "
            + $"across {surface.Files} file(s).");

        if (surface.Types.Count > 0)
        {
            Console.Out.WriteLine(
                $"  {surface.TypesForMostOfIt} type(s) and {surface.FilesForMostOfIt} "
                + "file(s) carry four fifths of it.");

            foreach (var type in surface.Types.Take(10))
            {
                Console.Out.WriteLine($"    {type.Uses,5}  {type.Name} ({type.Files} file(s))");
            }
        }

        // What could replace it, scored against what is actually used.
        var catalogue = Successors.Load();
        var ranked = new Successors().Rank(surface, catalogue);

        if (ranked.Count == 0)
        {
            Console.Out.WriteLine(
                $"  No candidate replacement catalogued ({catalogue.Source}).");
        }

        foreach (var coverage in ranked)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(
                $"  -> {(coverage.Candidate.Length == 0 ? "nothing, and nothing is needed" : coverage.Candidate)}"
                + $": covers {coverage.Percent}% of the calls");
            Console.Out.WriteLine($"     {coverage.Note}");

            if (coverage.Blocked)
            {
                Console.Out.WriteLine(
                    $"     {coverage.Unavailable.Count} type(s), {coverage.UsesUnavailable} call(s), "
                    + "have no replacement at all:");

                foreach (var use in coverage.Unavailable.Take(6))
                    Console.Out.WriteLine($"       {use.Uses,5}  {use.Name} ({use.Files} file(s))");
            }

            if (coverage.Unknown.Count > 0)
            {
                Console.Out.WriteLine(
                    $"     {coverage.Unknown.Count} type(s), {coverage.UsesUnknown} call(s), are not "
                    + "in the catalogue. Unknown, which is not the same as fine.");
            }
        }

        foreach (var note in surface.Notes) Console.Error.WriteLine($"  {note}");
        Console.Out.WriteLine();
    }

    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

var ollamaUrl = builder.Configuration["OLLAMA_URL"] ?? "http://localhost:11434";

builder.Services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>(client =>
{
    client.BaseAddress = new Uri(ollamaUrl);
    client.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddHttpClient<IChatClient, OllamaChatClient>(client =>
{
    client.BaseAddress = new Uri(ollamaUrl);
    // Generous, because on a CPU-only machine a 3B model answering from 8k of
    // context is measured in tens of seconds, not hundreds of milliseconds.
    client.Timeout = TimeSpan.FromMinutes(10);
});

// Named rather than typed: the hosted client is built per request around a
// key that arrived with it, so it cannot be a registered service.
builder.Services.AddHttpClient("hosted", client =>
    client.Timeout = TimeSpan.FromMinutes(5));

builder.Services.AddSingleton<IChatClients, ChatClients>();

builder.Services.AddSingleton<SourceWalker>();
builder.Services.AddSingleton<CodeChunker>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<IVectorStore, SqliteVectorStore>();
builder.Services.AddScoped<Retriever>();
builder.Services.AddScoped<IngestionService>();
builder.Services.AddScoped<AnswerService>();
builder.Services.AddSingleton<IngestionJobs>();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration["CORS_ORIGIN"] ?? "http://localhost:4200")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// The schema is brought forward once, before any request can read a table
// whose shape has changed. Constructing the store opens the file and creates
// what is missing; this adds the workspace column, the index and the rebuilt
// ledger on top of an index that predates them.
_ = new Workspaces(app.Services.GetRequiredService<IVectorStore>().Connection);

app.UseCors();

// Workspaces and the ledger run their statements on the store's connection
// directly, so they take the same gate the store takes for its own. Never call
// back into the store from inside one of these: the gate is not reentrant, and
// doing so deadlocks the request rather than failing it.
async Task<T> WithConnection<T>(IVectorStore store, Func<T> work)
{
    await store.Gate.WaitAsync();
    try { return work(); }
    finally { store.Gate.Release(); }
}

app.MapGet("/api/health", async (IVectorStore store) =>
{
    // Counted per workspace rather than in total. One number over a file
    // holding three projects answers a question nobody asked.
    var workspaces = await WithConnection(store, () => new Workspaces(store.Connection).All());

    return Results.Ok(new
    {
        status = "ok",
        indexedChunks = workspaces.Sum(w => w.Chunks),
        workspaces = workspaces.Select(w => new { w.Id, w.Name, w.Chunks }),
    });
});

app.MapPost("/api/ingest", async (
    IngestRequest request, IngestionService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    try
    {
        return Results.Ok(await service.IngestAsync(request.Path, request.Workspace, ct: ct));
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return ModelUnreachable(ex);
    }
});

// Indexing, started and left to run.
//
// The synchronous /api/ingest above stays: a script that wants to block until
// the index is built should be able to, and the CI does exactly that. This is
// the same work for a reader who would rather ask questions while it happens.
// What the interface should offer for generation, and the sentence it owes the
// reader before they pick the hosted option.
app.MapGet("/api/models", (IChatClients chats) =>
{
    var options = chats.Options;

    return Results.Ok(new
    {
        local = new
        {
            model = options.LocalModel,
            description = "Runs here. Nothing leaves this machine.",
        },
        hosted = new
        {
            available = options.HostedAvailable,
            url = options.HostedUrl,
            model = options.DefaultHostedModel,
            description = "Your own API key, used for the request and never stored.",
            warning = "The question and the excerpts retrieved for it are sent to "
                    + options.HostedUrl + ". Your code is not uploaded, but the "
                    + "parts quoted in an answer do leave this machine.",
        },
        // Stated plainly because it is the part people assume is negotiable.
        embeddings = "Always local. Embedding reads every file, so sending it "
                   + "out would upload the whole codebase.",
    });
});

app.MapPost("/api/ingest/start", (IngestRequest request, IngestionJobs jobs) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    if (!Directory.Exists(request.Path))
        return Results.BadRequest(new { error = $"No such directory: {request.Path}." });

    var started = jobs.Start(request.Workspace, request.Path);

    // One run at a time: a single embedding already saturates every core, so a
    // second concurrent run does not halve the wait, it doubles both.
    return started is null
        ? Results.Conflict(new
        {
            error = "Something is already being indexed.",
            hint = "Wait for it to finish, or cancel it first.",
            running = jobs.Busy(),
        })
        : Results.Accepted($"/api/ingest/status?workspace={request.Workspace}", started);
});

app.MapGet("/api/ingest/status", (IngestionJobs jobs, string? workspace = null) =>
{
    var job = jobs.Status(workspace ?? Workspaces.Default);

    // Not an error: a workspace that has never been indexed has no run to
    // report, and the caller polling for one should be told that plainly.
    return job is null ? Results.NoContent() : Results.Ok(job);
});

app.MapPost("/api/ingest/cancel", (IngestionJobs jobs, string? workspace = null) =>
    jobs.Cancel(workspace ?? Workspaces.Default)
        ? Results.NoContent()
        : Results.NotFound(new { error = "Nothing is being indexed for that workspace." }));

app.MapPost("/api/ask", async (
    AskRequest request, AnswerService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "A question is required." });

    try
    {
        return Results.Ok(await service.AnswerAsync(request, request.Workspace, ct));
    }
    catch (HttpRequestException ex)
    {
        return ModelUnreachable(ex);
    }
});

// The same answer, streamed as server-sent events.
//
// SSE rather than WebSockets: the traffic is one-way, it survives proxies that
// mangle upgrade requests, and the browser reconnects on its own. A WebSocket
// would add a protocol for nothing.
app.MapPost("/api/ask/stream", async (
    AskRequest request, AnswerService service, HttpContext context, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "A question is required." }, ct);
        return;
    }

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    // Tells nginx and friends not to buffer, which would hold every token back
    // until the answer finished and undo the whole point.
    context.Response.Headers["X-Accel-Buffering"] = "no";

    // The same conventions ASP.NET Core applies to its own JSON responses.
    // Serialising by hand here would otherwise emit PascalCase while /api/ask
    // emits camelCase, and a client reading both would break on one of them.
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    await foreach (var item in service.StreamAsync(request, request.Workspace, ct))
    {
        var (name, payload) = item switch
        {
            AnswerEvent.Sources s => ("sources", JsonSerializer.Serialize(s.Citations, json)),
            AnswerEvent.Token t   => ("token", JsonSerializer.Serialize(t.Text, json)),
            AnswerEvent.Failed f  => ("failed", JsonSerializer.Serialize(f.Message, json)),
            _                     => ("done", "{}"),
        };

        await context.Response.WriteAsync($"event: {name}\ndata: {payload}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
});

// The text behind a citation.
//
// A citation the reader cannot open is a claim they have to take on trust,
// which is the opposite of what citing sources is for.
app.MapGet("/api/excerpt", async (
    string path, int line, IVectorStore store, CancellationToken ct, string? workspace = null) =>
{
    var chunk = await store.ExcerptAsync(path, line, workspace ?? Workspaces.Default, ct);

    return chunk is null
        ? Results.NotFound(new { error = $"No indexed chunk at {path}:{line}." })
        : Results.Ok(new
        {
            filePath = chunk.FilePath,
            startLine = chunk.StartLine,
            endLine = chunk.EndLine,
            content = chunk.Content,
        });
});

// The assessment, as the document rather than as JSON.
//
// Markdown is the response body, not a field inside one. What comes back is
// meant to be written to a file, converted to PDF or pasted into a ticket, and
// wrapping it in JSON would mean every caller unescaping it first.
//
// No model is involved, and neither is the index: this answers on a repository
// that has never been ingested, in seconds rather than hours.
app.MapPost("/api/report", (ReportRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    try
    {
        var assessment = new Assessor { HistoryMonths = request.HistoryMonths }
            .Assess(request.Path);

        return Results.Text(
            new ReportWriter { TopRisks = request.Top }.Write(assessment),
            "text/markdown",
            System.Text.Encoding.UTF8);
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// How much of a modernisation is mechanical, and what no automation will do.
// Reads project files only: no compilation, no restore, no call to nuget.org,
// so it answers on a solution that does not build.
// Where a strangler fig could put the new implementation beside the old, and
// where it could not. Reads source only: no compilation, so it answers on a
// solution that does not build.
// One index per project. Without them a second project means deleting the
// first, or asking questions of a mixture of the two.
app.MapGet("/api/workspaces", async (IVectorStore store) =>
    Results.Ok(await WithConnection(store, () => new Workspaces(store.Connection).All())));

app.MapPost("/api/workspaces", async (
    CreateWorkspaceRequest request, IVectorStore store, IngestionJobs jobs) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "A name is required." });

    if (!string.IsNullOrWhiteSpace(request.RepositoryUrl)
        && !Cloning.IsAcceptable(request.RepositoryUrl))
    {
        return Results.BadRequest(new
        {
            error = "Only http and https repository URLs are accepted.",
            hint = "git understands transports that run commands or read local "
                 + "disk, and a URL typed into a form should reach neither.",
        });
    }

    var created = await WithConnection(store, () =>
        new Workspaces(store.Connection).Create(request.Name, request.RootPath ?? string.Empty));

    // A repository has to be fetched before there is anything to index, and
    // that is minutes on a large one. It runs as a job for the same reason
    // indexing does, rather than holding this request open.
    if (!string.IsNullOrWhiteSpace(request.RepositoryUrl))
    {
        var cloneRoot = builder.Configuration["CLONE_PATH"] ?? "repos";

        var started = jobs.StartFromRepository(
            created.Id,
            new IngestionJobs.CloneSpec(request.RepositoryUrl, request.Token, cloneRoot));

        if (started is null)
        {
            // The workspace exists but nothing will fill it, which is worse
            // than not creating it: it would sit empty and look indexed.
            await WithConnection(store, () => new Workspaces(store.Connection).Delete(created.Id));

            return Results.Conflict(new
            {
                error = "Something is already being indexed.",
                hint = "Wait for it to finish, or cancel it, then add this one.",
                running = jobs.Busy(),
            });
        }
    }

    return Results.Created($"/api/workspaces/{created.Id}", created);
});

// Deletes the chunks with it. A workspace row removed on its own would leave
// chunks nothing can name, which is worse than either outcome.
app.MapDelete("/api/workspaces/{id}", async (string id, IVectorStore store, IngestionJobs jobs) =>
{
    // The run goes first. Left going, it would keep writing chunks into a
    // workspace that no longer exists, and they would never be found again.
    jobs.Forget(id);

    return await WithConnection(store, () => new Workspaces(store.Connection).Delete(id))
        ? Results.NoContent()
        : Results.NotFound(new { error = $"No workspace {id}." });
});

// The mechanical conversions, as a patch nobody has applied.
//
// The same code the `convert` command runs. A route as well, so the interface
// can show a diff beside the survey that argued for it.
app.MapPost("/api/convert", (ConvertRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    if (!Directory.Exists(request.Path))
        return Results.BadRequest(new { error = $"No such directory: {request.Path}." });

    try
    {
        var outcome = Conversions.For(request.Kind, request.Path);

        return Results.Ok(new
        {
            outcome.Kind,
            outcome.Patch,
            outcome.Notes,
            // Named separately because on a real estate this is the longer
            // list, and it is the one that decides what the work actually is.
            outcome.Refusals,
            empty = outcome.Empty,
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message, kinds = Conversions.Kinds });
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/seams", (SeamsRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    try
    {
        var survey = new SolutionAnalysis().Seams(request.Path);

        return Results.Ok(new
        {
            types = new
            {
                total = survey.Total,
                substitutable = survey.Substitutable,
                afterExtraction = survey.AfterExtraction,
                notWithoutRewrite = survey.NotWithoutRewrite,
            },
            // What holds the most types shut. Four names accounting for half an
            // estate is a different plan from forty accounting for one each.
            closedBy = survey.ClosedBy.Take(request.Top).Select(c => new { c.Name, c.Types }),
            // The refusals, worst first. Listing what can be cut is easy; this
            // is the half that changes what someone decides.
            refused = survey.Types
                .Where(t => t.Verdict == SeamVerdict.NotWithoutRewrite)
                .OrderByDescending(t => t.AmbientUses)
                .Take(request.Top)
                .Select(t => new { t.Name, t.Path, t.Reason, ambients = t.Ambients }),
        });
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/modernise", (ModerniseRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    try
    {
        var survey = new Modernisation().Survey(request.Path);

        return Results.Ok(new
        {
            projects = new
            {
                total = survey.Projects.Count,
                preSdk = survey.PreSdk,
                sdkStyle = survey.SdkStyle,
                blocked = survey.Blocked,
                convertibleAsIs = survey.ConvertibleAsIs,
            },
            packaging = new
            {
                packagesConfig = survey.UsingPackagesConfig,
                packageReference = survey.UsingPackageReference,
                bindingRedirects = survey.BindingRedirects,
            },
            packages = new
            {
                references = survey.References,
                distinct = survey.Packages.Count,
                divergent = survey.Divergent,
                portable = survey.ReferencesPortable,
                tiedToSystemWeb = survey.ReferencesTiedToSystemWeb,
                // Reported separately from portable: an unknown package sold
                // as fine is a problem discovered after the price is agreed.
                unclassified = survey.ReferencesUnknown,
            },
            // Old but coherent is a different job from old and drifted. The
            // distinction belongs in the answer, not in the reader's head.
            tended = survey.Tended,
            deadEnds = survey.DeadEnds.Select(p => new { p.Id, p.Projects }),
            unknown = survey.Packages
                .Where(p => p.Portability == Portability.Unknown)
                .OrderByDescending(p => p.Projects)
                .Take(request.TopUnknown)
                .Select(p => new { p.Id, p.Projects, p.Versions }),
        });
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Structural analysis. Unlike /api/ask this touches no model at all: the
// project files and the folder layout are read directly, which is why it
// answers in milliseconds and cannot invent anything.
app.MapPost("/api/map", (MapRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    try
    {
        var map = new ProjectGraph().Build(request.Path);
        var mermaid = new MermaidWriter
        {
            MinimumLines = request.MinimumLines,
            IncludeTests = request.IncludeTests,
        }.Write(map);

        return Results.Ok(new MapResponse(
            map.Projects.Count,
            map.TotalFiles,
            map.TotalLines,
            map.Edges.Count,
            Findings.Detect(map)
                .Select(f => (object)new { kind = f.Kind.ToString(), f.Project, f.Summary, f.Detail })
                .ToList(),
            mermaid));
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Where the code is likely to hurt. Complexity from Roslyn, change frequency
// from git, test coverage by naming convention. No model involved: every
// number here is measured, and the ones that could not be measured say so.
app.MapPost("/api/risk", (RiskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    try
    {
        var report = new SolutionAnalysis
        {
            MinimumCodeLines = request.MinimumCodeLines,
            HistoryMonths = request.HistoryMonths,
        }.Analyse(request.Path);

        return Results.Ok(new
        {
            history = new { status = report.HistoryStatus.ToString(), note = report.HistoryNote },
            generatedFilesExcluded = report.GeneratedFilesExcluded,
            ranked = report.Entries.Count,
            entries = report.Entries.Take(request.Top),
        });
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// A class diagram, extracted rather than imagined. Types, members and
// relations come from the syntax tree; nothing is inferred by a model.
app.MapPost("/api/diagram", (DiagramRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    if (string.IsNullOrWhiteSpace(request.Namespace) && string.IsNullOrWhiteSpace(request.Type))
        return Results.BadRequest(new { error = "Give a namespace or a type. A diagram of "
                                              + "every type in a solution is unreadable." });

    try
    {
        var map = new SolutionAnalysis().Types(request.Path);
        var writer = new ClassDiagramWriter { MaxMembers = request.MaxMembers };

        var mermaid = string.IsNullOrWhiteSpace(request.Type)
            ? writer.ForNamespace(map, request.Namespace!)
            : writer.Around(map, request.Type!);

        return Results.Ok(new
        {
            types = map.Types.Count,
            relations = map.Relations.Count,
            unresolvedBases = map.UnresolvedBases,
            mermaid,
        });
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/index", async (IVectorStore store, CancellationToken ct, string? workspace = null) =>
{
    await store.ClearAsync(workspace ?? Workspaces.Default, ct);
    // The ledger has to go too. Left behind, it would report every file as
    // already indexed and the next ingest would do nothing at all. Outside the
    // ClearAsync above rather than beside it: that call takes the gate itself.
    await WithConnection(store, () =>
    {
        new IngestionLedger(store.Connection, workspace ?? Workspaces.Default).Clear();
        return true;
    });
    return Results.NoContent();
});

// Ollama being down is the single most likely thing to go wrong, and a bare 500
// with an empty body sends the reader looking in the wrong place.
IResult ModelUnreachable(HttpRequestException exception) => Results.Json(
    new
    {
        error = $"Could not reach the model at {ollamaUrl}.",
        hint = "Start Ollama, and check the models are pulled: "
             + "`docker compose exec ollama ollama pull nomic-embed-text`.",
        detail = exception.Message,
    },
    statusCode: StatusCodes.Status503ServiceUnavailable);

app.Run();
