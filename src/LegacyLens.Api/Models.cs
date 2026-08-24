namespace LegacyLens.Api;

/// <summary>A slice of source code, with enough location data to cite it.</summary>
public record Chunk(
    string Id,
    string FilePath,
    int StartLine,
    int EndLine,
    string Content)
{
    /// <summary>
    /// The text actually sent to the embedding model. A bare fragment of code
    /// embeds poorly on its own, the path carries a lot of the meaning.
    /// </summary>
    public string EmbeddingText => $"File: {FilePath}\n\n{Content}";
}

public record EmbeddedChunk(Chunk Chunk, float[] Embedding);

/// <summary>Which search found a chunk. Both is the strongest signal.</summary>
public enum MatchSource { Vector, Text, Both }

public record SearchHit(Chunk Chunk, float Score, MatchSource Source = MatchSource.Vector);

public record Citation(
    string FilePath,
    int StartLine,
    int EndLine,
    float Score,
    /// <summary>
    /// How this chunk was retrieved. A chunk found by text alone has no
    /// meaningful cosine score, and showing one would invent a number.
    /// </summary>
    string FoundBy);

public record IngestRequest(string Path, string Workspace = "default");

public record IngestResponse(int FilesRead, int ChunksIndexed, long ElapsedMs);

/// <summary>
/// How far an indexing run has got, counted against the files that need work
/// rather than the files found.
/// </summary>
public record IngestionProgress(int FilesTotal, int FilesDone, int ChunksIndexed, string? CurrentFile);

public record AskRequest(
    string Question,
    int TopK = 6,
    string Workspace = "default",
    /// <summary>
    /// Which model answers. Absent means the local one, which is the default
    /// and the only setting under which no part of the code leaves the machine.
    /// </summary>
    LegacyLens.Api.Generation.ModelChoice? Model = null);

public record AskResponse(string Answer, IReadOnlyList<Citation> Sources);

public record MapRequest(string Path, int MinimumLines = 500, bool IncludeTests = false);

public record MapResponse(
    int Projects,
    int Files,
    int Lines,
    int Dependencies,
    IReadOnlyList<object> Findings,
    string Mermaid);

public record RiskRequest(string Path, int MinimumCodeLines = 100, int HistoryMonths = 24, int Top = 20);

public record DiagramRequest(string Path, string? Namespace = null, string? Type = null, int MaxMembers = 8);

public record ModerniseRequest(string Path, int TopUnknown = 15);

/// <summary>
/// One mechanical conversion to propose.
///
/// <paramref name="Kind"/> is one at a time on purpose: two of them rewrite the
/// same project file, so a patch carrying both cannot apply.
/// </summary>
public record ConvertRequest(string Path, string Kind);

/// <summary>
/// A conversion to put on a branch of its own.
///
/// The patch is regenerated here rather than sent back by the caller: a patch
/// that travelled to a browser and back is a patch nobody can prove is the one
/// that was read.
/// </summary>
public record ApplyRequest(string Path, string Kind);

/// <summary>
/// What a codebase uses of its dependencies, and what could replace them.
///
/// One call rather than three. The surface, the candidates and their coverage
/// are one question with one answer, and splitting them across endpoints makes
/// the interface responsible for reassembling a thought.
/// </summary>
public record SurfaceRequest(string Path, string? Package = null);

/// <summary>
/// One file to rewrite, and the package to move it off.
///
/// One file rather than a folder, on purpose. These rewrites are repetitive:
/// a reader who sees one before and after knows what the other forty-six cost,
/// and forty-seven of them is a wait nobody sits through.
/// </summary>
public record ProjectRequest(
    string Path,
    string Package,
    /// <summary>
    /// The solution this file belongs to, so a type from the project can be
    /// told from a type that was invented. Without it every unresolved name
    /// looks made up.
    /// </summary>
    string? Root = null,
    LegacyLens.Api.Generation.ModelChoice? Model = null);

/// <summary>Where the code can be cut, and what closes the cut.</summary>
public record SeamsRequest(string Path, int Top = 20);

/// <summary>
/// A request for the assessment document.
///
/// <paramref name="Top"/> bounds the ranked file table. The rest of the report
/// has no knob on purpose: a document whose sections are negotiable per call is
/// a document nobody can compare against last month's.
/// </summary>
public record ReportRequest(string Path, int Top = 15, int HistoryMonths = 24);

/// <summary>
/// A project to index on its own.
///
/// Either a folder the API can already see, or a repository to fetch. The
/// token, when there is one, is used for that fetch and for nothing else: it
/// is not written to the index, to the workspace row, to the clone's git
/// config or to the log.
/// </summary>
public record CreateWorkspaceRequest(
    string Name,
    string? RootPath = null,
    string? RepositoryUrl = null,
    string? Token = null);
