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

builder.Services.AddSingleton<SourceWalker>();
builder.Services.AddSingleton<CodeChunker>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<IVectorStore, SqliteVectorStore>();
builder.Services.AddScoped<Retriever>();
builder.Services.AddScoped<IngestionService>();
builder.Services.AddScoped<AnswerService>();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration["CORS_ORIGIN"] ?? "http://localhost:4200")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.MapGet("/api/health", async (IVectorStore store, CancellationToken ct) =>
    Results.Ok(new { status = "ok", indexedChunks = await store.CountAsync(ct) }));

app.MapPost("/api/ingest", async (
    IngestRequest request, IngestionService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "A path is required." });

    try
    {
        return Results.Ok(await service.IngestAsync(request.Path, ct));
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

app.MapPost("/api/ask", async (
    AskRequest request, AnswerService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "A question is required." });

    try
    {
        return Results.Ok(await service.AnswerAsync(request, ct));
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

    await foreach (var item in service.StreamAsync(request, ct))
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
    string path, int line, IVectorStore store, CancellationToken ct) =>
{
    var chunk = await store.ExcerptAsync(path, line, ct);

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

app.MapDelete("/api/index", async (IVectorStore store, CancellationToken ct) =>
{
    await store.ClearAsync(ct);
    // The ledger has to go too. Left behind, it would report every file as
    // already indexed and the next ingest would do nothing at all.
    new IngestionLedger(store.Connection).Clear();
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
