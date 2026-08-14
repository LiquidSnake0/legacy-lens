using LegacyLens.Analysis;
using LegacyLens.Api;
using LegacyLens.Api.Embeddings;
using LegacyLens.Api.Generation;
using LegacyLens.Api.Ingestion;
using LegacyLens.Api.Storage;

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

app.MapDelete("/api/index", async (IVectorStore store, CancellationToken ct) =>
{
    await store.ClearAsync(ct);
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
