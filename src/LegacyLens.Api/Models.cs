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

public record IngestRequest(string Path);

public record IngestResponse(int FilesRead, int ChunksIndexed, long ElapsedMs);

public record AskRequest(string Question, int TopK = 6);

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
