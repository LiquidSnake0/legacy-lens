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
        if (string.IsNullOrWhiteSpace(content)) return [];

        // Split on '\n' only. A trailing '\r' stays at the end of its line and
        // survives the round trip, so a citation still matches the file byte
        // for byte on Windows-authored source.
        var lines = content.Split('\n');
        var depths = BraceDepths(lines);
        var chunks = new List<Chunk>();

        var start = 0;
        while (start < lines.Length)
        {
            // 1. Take lines while the budget holds. +1 for the newline that
            //    will join them back together.
            var end = start;
            var size = 0;
            while (end < lines.Length && size + lines[end].Length + 1 <= MaxChars)
            {
                size += lines[end].Length + 1;
                end++;
            }

            // A single line longer than the entire budget: minified JavaScript,
            // a generated constant table. Take it whole. An oversized chunk is
            // a bad chunk; a skipped line is a hole in the index, and holes are
            // invisible until someone asks about exactly that code.
            if (end == start) end = start + 1;

            // 2. Back off to a structural boundary — unless the slice already
            //    reaches the end of the file, where there is nothing to
            //    preserve by cutting earlier.
            var atEof = end >= lines.Length;
            var cut = atEof ? lines.Length - 1 : BestBoundary(lines, depths, start, end);

            // 3. Emit. The content is the joined lines, unmodified: reformat
            //    anything and the line numbers stop describing the file.
            var text = string.Join('\n', lines[start..(cut + 1)]);
            if (!string.IsNullOrWhiteSpace(text))
            {
                chunks.Add(new Chunk(
                    MakeId(filePath, start + 1), filePath, start + 1, cut + 1, text));
            }

            if (cut >= lines.Length - 1) break;

            // 4. Advance, repeating a few lines so a declaration landing on a
            //    boundary is not orphaned from its body. Always by at least one
            //    line — otherwise a short chunk plus a long overlap loops here
            //    forever.
            start = Math.Max(start + 1, cut + 1 - OverlapLines);
        }

        return chunks;
    }

    /// <summary>
    /// The best place to cut within the last part of the slice, or the end of
    /// the slice if nothing better presents itself.
    /// </summary>
    private static int BestBoundary(string[] lines, int[] depths, int start, int end)
    {
        // Only the last fifth is considered. Reaching further back finds nicer
        // boundaries at the cost of throwing away content that had already
        // earned its place in the chunk.
        var window = Math.Max(1, (end - start) / 5);
        var limit = Math.Max(start, end - window);

        var best = -1;
        var bestQuality = 0;

        // Walking backwards with a strict comparison keeps the latest line of
        // the best available quality — the fullest chunk that still cuts well.
        for (var i = end - 1; i >= limit; i--)
        {
            var quality = BoundaryQuality(lines, depths, i);
            if (quality > bestQuality)
            {
                bestQuality = quality;
                best = i;
            }
        }

        return best >= 0 ? best : end - 1;
    }

    /// <summary>How good a cut after this line would be. Higher is better.</summary>
    private static int BoundaryQuality(string[] lines, int[] depths, int index)
    {
        if (string.IsNullOrWhiteSpace(lines[index]))
        {
            // A blank line back at depth zero ends a type or a namespace; at
            // depth one, a method inside a class. Both are real seams that the
            // author put there.
            return depths[index] switch { 0 => 4, 1 => 3, _ => 2 };
        }

        return lines[index].TrimEnd().EndsWith('}') ? 1 : 0;
    }

    /// <summary>
    /// Brace depth after each line.
    ///
    /// This counts braces inside string literals and comments, so it is wrong.
    /// It does not need to be right: it is picking a plausible place to cut, not
    /// parsing the language. A real parser is the alternative, and it costs one
    /// implementation per language in a tool whose whole point is reading code
    /// nobody wants to touch.
    /// </summary>
    private static int[] BraceDepths(string[] lines)
    {
        var depths = new int[lines.Length];
        var depth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var character in lines[i])
            {
                if (character == '{') depth++;
                else if (character == '}') depth--;
            }

            if (depth < 0) depth = 0;
            depths[i] = depth;
        }

        return depths;
    }

    /// <summary>
    /// Stable identifier for a chunk, so re-indexing the same file overwrites
    /// rather than duplicating.
    /// </summary>
    public static string MakeId(string filePath, int startLine) =>
        $"{filePath}#{startLine}";
}
