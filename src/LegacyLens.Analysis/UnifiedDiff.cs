using System.Text;

namespace LegacyLens.Analysis;

/// <summary>
/// Writes patches `git apply` accepts.
///
/// Written here rather than taken from a package because the output is the
/// product: a patch a person reads before deciding, and a dependency that
/// reformats a file to make its own diff smaller would defeat that. The
/// algorithm is a plain longest common subsequence, which is quadratic and
/// therefore bounded below, because project files are hundreds of lines and
/// nothing here should ever run on a megabyte of XML.
/// </summary>
public static class UnifiedDiff
{
    /// <summary>Above this, the quadratic table stops being reasonable.</summary>
    public const int MaxLines = 20_000;

    private const int Context = 3;

    /// <summary>
    /// A patch turning <paramref name="before"/> into <paramref name="after"/>,
    /// or an empty string when they are identical, so callers can treat
    /// "nothing to do" as falsy rather than as an empty hunk.
    /// </summary>
    public static string Between(string relativePath, string before, string after)
    {
        if (before == after) return string.Empty;

        var (a, aEndsWithNewline) = Split(before);
        var (b, bEndsWithNewline) = Split(after);

        if (a.Length > MaxLines || b.Length > MaxLines)
            throw new InvalidOperationException(
                $"{relativePath}: {Math.Max(a.Length, b.Length)} lines exceeds the {MaxLines} limit.");

        var edits = Diff(a, b);
        if (edits.All(e => e.Kind == EditKind.Same)) return string.Empty;

        var patch = new StringBuilder();
        patch.Append($"diff --git a/{relativePath} b/{relativePath}\n");
        patch.Append($"--- a/{relativePath}\n");
        patch.Append($"+++ b/{relativePath}\n");
        WriteHunks(patch, edits, a.Length, b.Length, aEndsWithNewline, bEndsWithNewline);
        return patch.ToString();
    }

    /// <summary>
    /// A patch removing a file. Written separately because a deletion has no
    /// "after" side and git wants `/dev/null` rather than an empty file.
    /// </summary>
    public static string Deleting(string relativePath, string content)
    {
        var (lines, endsWithNewline) = Split(content);
        var patch = new StringBuilder();
        patch.Append($"diff --git a/{relativePath} b/{relativePath}\n");
        patch.Append("deleted file mode 100644\n");
        patch.Append($"--- a/{relativePath}\n");
        patch.Append("+++ /dev/null\n");
        patch.Append($"@@ -1,{lines.Length} +0,0 @@\n");
        foreach (var line in lines) patch.Append($"-{line}\n");
        if (!endsWithNewline) patch.Append(NoNewline);
        return patch.ToString();
    }

    /// <summary>
    /// A patch adding a file. The mirror of <see cref="Deleting"/>: git wants
    /// `/dev/null` on the "before" side rather than an empty file.
    /// </summary>
    public static string Creating(string relativePath, string content)
    {
        var (lines, endsWithNewline) = Split(content);
        var patch = new StringBuilder();
        patch.Append($"diff --git a/{relativePath} b/{relativePath}\n");
        patch.Append("new file mode 100644\n");
        patch.Append("--- /dev/null\n");
        patch.Append($"+++ b/{relativePath}\n");
        patch.Append($"@@ -0,0 +1,{lines.Length} @@\n");
        foreach (var line in lines) patch.Append($"+{line}\n");
        if (!endsWithNewline) patch.Append(NoNewline);
        return patch.ToString();
    }

    private const string NoNewline = "\\ No newline at end of file\n";

    /// <summary>
    /// Splits on the line feed only, leaving any carriage return attached to
    /// the line it belongs to.
    ///
    /// Normalising CRLF to LF here would produce a patch that git rejects on
    /// every Windows-authored file, which is most of the estate this tool is
    /// pointed at. The comparison git performs is byte for byte.
    ///
    /// The final newline is tracked rather than trimmed, because a file that
    /// does not end with one needs the `\ No newline at end of file` marker
    /// and a patch without it does not apply. Orchard's project files are
    /// written that way, so this is not a hypothetical.
    /// </summary>
    private static (string[] Lines, bool EndsWithNewline) Split(string text)
    {
        if (text.Length == 0) return ([], true);

        var endsWithNewline = text[^1] == '\n';
        var body = endsWithNewline ? text[..^1] : text;
        return (body.Split('\n'), endsWithNewline);
    }

    private enum EditKind { Same, Removed, Added }

    private record Edit(EditKind Kind, string Text);

    private static List<Edit> Diff(string[] a, string[] b)
    {
        // lcs[i, j] is the length of the longest common subsequence of a[i..]
        // and b[j..]. Filled backwards so the walk forward reads naturally.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var edits = new List<Edit>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                edits.Add(new Edit(EditKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                edits.Add(new Edit(EditKind.Removed, a[x]));
                x++;
            }
            else
            {
                edits.Add(new Edit(EditKind.Added, b[y]));
                y++;
            }
        }

        while (x < a.Length) edits.Add(new Edit(EditKind.Removed, a[x++]));
        while (y < b.Length) edits.Add(new Edit(EditKind.Added, b[y++]));
        return edits;
    }

    private static void WriteHunks(
        StringBuilder patch,
        List<Edit> edits,
        int oldTotal,
        int newTotal,
        bool oldEndsWithNewline,
        bool newEndsWithNewline)
    {
        var changed = edits
            .Select((e, i) => (Edit: e, Index: i))
            .Where(x => x.Edit.Kind != EditKind.Same)
            .Select(x => x.Index)
            .ToList();

        var groups = new List<(int Start, int End)>();
        foreach (var index in changed)
        {
            var start = Math.Max(0, index - Context);
            var end = Math.Min(edits.Count - 1, index + Context);

            // Overlapping context means one hunk, not two: git rejects hunks
            // whose line numbers run backwards over each other.
            if (groups.Count > 0 && start <= groups[^1].End + 1)
                groups[^1] = (groups[^1].Start, Math.Max(groups[^1].End, end));
            else
                groups.Add((start, end));
        }

        foreach (var (start, end) in groups)
        {
            int oldStart = 1, newStart = 1;
            for (var i = 0; i < start; i++)
            {
                if (edits[i].Kind != EditKind.Added) oldStart++;
                if (edits[i].Kind != EditKind.Removed) newStart++;
            }

            var oldCount = 0;
            var newCount = 0;
            for (var i = start; i <= end; i++)
            {
                if (edits[i].Kind != EditKind.Added) oldCount++;
                if (edits[i].Kind != EditKind.Removed) newCount++;
            }

            patch.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");

            // Line numbers on each side, tracked while emitting so the last
            // line of either file can be recognised and marked.
            var oldLine = oldStart - 1;
            var newLine = newStart - 1;

            for (var i = start; i <= end; i++)
            {
                var kind = edits[i].Kind;
                var prefix = kind switch
                {
                    EditKind.Same => " ",
                    EditKind.Removed => "-",
                    _ => "+",
                };

                if (kind != EditKind.Added) oldLine++;
                if (kind != EditKind.Removed) newLine++;

                patch.Append($"{prefix}{edits[i].Text}\n");

                var lastOfOld = kind != EditKind.Added && oldLine == oldTotal && !oldEndsWithNewline;
                var lastOfNew = kind != EditKind.Removed && newLine == newTotal && !newEndsWithNewline;
                if (lastOfOld || lastOfNew) patch.Append(NoNewline);
            }
        }
    }
}
