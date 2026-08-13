namespace LegacyLens.Api.Ingestion;

/// <summary>
/// Cuts a source file into chunks small enough to embed, large enough to mean
/// something on their own.
/// </summary>
public class CodeChunker
{
    /// <summary>Target size of a chunk, in characters.</summary>
    public int MaxChars { get; }

    /// <summary>
    /// Lines repeated between neighbouring chunks, so a definition landing
    /// exactly on a boundary is not cut in half.
    /// </summary>
    public int OverlapLines { get; }

    public CodeChunker(int maxChars = 1_500, int overlapLines = 3)
    {
        MaxChars = maxChars;
        OverlapLines = overlapLines;
    }

    /// <summary>
    /// Splits <paramref name="content"/> into chunks. Line numbers are 1-based
    /// and inclusive, because that is what an editor shows and the whole point
    /// is that a citation can be opened and checked.
    /// </summary>
    public IReadOnlyList<Chunk> Split(string filePath, string content)
    {
        // ---------------------------------------------------------------
        // TODO — see docs/TODO.md #1.
        //
        // Tests in tests/LegacyLens.Tests/CodeChunkerTests.cs describe what
        // this needs to do. They fail until it does.
        //
        // The naive version — cut every _maxChars characters — passes almost
        // none of them, and that is intentional: it is the version that makes
        // retrieval quality bad, and the tests are written to catch it.
        // ---------------------------------------------------------------
        throw new NotImplementedException(
            "CodeChunker.Split is not implemented yet — see docs/TODO.md #1.");
    }

    /// <summary>
    /// Stable identifier for a chunk, so re-indexing the same file overwrites
    /// rather than duplicating.
    /// </summary>
    public static string MakeId(string filePath, int startLine) =>
        $"{filePath}#{startLine}";
}
