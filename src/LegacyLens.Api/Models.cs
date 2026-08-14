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

public record SearchHit(Chunk Chunk, float Score);

public record Citation(string FilePath, int StartLine, int EndLine, float Score);

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
