namespace LegacyLens.Api.Ingestion;

/// <summary>
/// Brace depth after each line, counting only the braces that are code.
///
/// A brace inside a string, a character literal or a comment is not
/// structure: <c>"{0} items"</c> in a format string used to open a block that
/// never closed, and every boundary after it in the file was judged one level
/// too deep. This is a small lexer, not a parser: it knows how each family of
/// languages writes strings and comments, and nothing else. Anything it
/// misreads costs a slightly worse cut, never a lost line.
///
/// Languages without braces, Python above all, carry their structure in the
/// indentation instead. For those the depth after a line is the indentation
/// level of the next line of code: cutting there lands exactly where the
/// author dedented, a blank line between two top-level functions is at depth
/// zero and a blank line inside a body is not.
/// </summary>
internal static class BraceScanner
{
    /// <summary>Files whose structure is written in indentation rather than braces.</summary>
    private static readonly HashSet<string> IndentedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".rb", ".yml", ".yaml",
    };

    private enum Family { CLike, Css, Hash, Basic }

    public static int[] Depths(string filePath, string[] lines)
    {
        var family = FamilyOf(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var csharp = extension == ".cs";
        var preprocessed = extension is ".cs" or ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hpp" or ".m";
        var javascript = extension is ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx";

        var depths = new int[lines.Length];
        var depth = 0;

        // Whether a line starts in code rather than inside a multi-line string
        // or comment; only those lines say anything about indentation.
        var startsInCode = new bool[lines.Length];

        // What is still open at the end of a line. Line comments and ordinary
        // strings end with their line; these are the constructs that do not.
        var inBlockComment = false;
        string? closing = null;       // delimiter of an open multi-line string
        var escapes = false;          // whether a backslash escapes inside it
        var doubledQuote = false;     // whether "" escapes inside it (C# @"...")

        for (var row = 0; row < lines.Length; row++)
        {
            var line = lines[row];
            var i = 0;
            startsInCode[row] = !inBlockComment && closing is null;

            // #region application/* is a directive, not code: its /* would
            // open a comment that never closes.
            if (preprocessed && !inBlockComment && closing is null && line.TrimStart().StartsWith('#'))
            {
                depths[row] = depth;
                continue;
            }

            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0) { i = line.Length; break; }
                    inBlockComment = false;
                    i = end + 2;
                    continue;
                }

                if (closing is not null)
                {
                    if (escapes && line[i] == '\\') { i += 2; continue; }
                    if (doubledQuote && line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    if (string.CompareOrdinal(line, i, closing, 0, closing.Length) == 0)
                    {
                        i += closing.Length;
                        closing = null;
                        continue;
                    }
                    i++;
                    continue;
                }

                var c = line[i];

                // Comments.
                if ((family == Family.CLike || family == Family.Css) && c == '/' && i + 1 < line.Length)
                {
                    // CSS has no line comments: a base64 image may contain //.
                    if (line[i + 1] == '/' && family == Family.CLike) break;
                    if (line[i + 1] == '*') { inBlockComment = true; i += 2; continue; }
                }
                if (javascript && c == '/' && StartsRegex(line, i))
                {
                    // A regular expression like /[^']+/ holds quotes that are
                    // not strings.
                    i = SkipRegex(line, i);
                    continue;
                }
                if (family == Family.Hash && c == '#') break;
                if (family == Family.Basic && c == '\'') break;

                // Strings that may span lines.
                var quotes = Run(line, i, '"');
                if (quotes >= 3 && (csharp || family == Family.Hash))
                {
                    // C# raw string literal, or a Python docstring: closed by
                    // the same run of quotes, escapes not interpreted.
                    closing = new string('"', quotes);
                    escapes = family == Family.Hash;
                    doubledQuote = false;
                    i += quotes;
                    continue;
                }
                if (family == Family.Hash && Run(line, i, '\'') >= 3)
                {
                    closing = "'''";
                    escapes = true;
                    doubledQuote = false;
                    i += 3;
                    continue;
                }
                if (csharp && IsVerbatimStart(line, i, out var prefix))
                {
                    closing = "\"";
                    escapes = false;
                    doubledQuote = true;
                    i += prefix;
                    continue;
                }
                if (javascript && c == '`')
                {
                    // JavaScript template literal. Whatever sits inside ${...}
                    // is balanced by construction, so skipping it whole costs
                    // nothing.
                    closing = "`";
                    escapes = true;
                    doubledQuote = false;
                    i++;
                    continue;
                }

                // Strings and character literals that end with their line.
                if (c == '"' || (c == '\'' && family != Family.Basic))
                {
                    i = SkipQuoted(line, i, c, backslashEscapes: family != Family.Basic);
                    continue;
                }

                if (c == '{') depth++;
                else if (c == '}') depth--;
                i++;
            }

            if (depth < 0) depth = 0;
            depths[row] = depth;
        }

        if (IndentedExtensions.Contains(extension)) AddIndentation(lines, startsInCode, depths);

        return depths;
    }

    /// <summary>
    /// Adds the indentation level of the next line of code to every line's
    /// depth. A blank line, a comment or a line inside a docstring takes the
    /// level of whatever code follows it, because that is what a cut after it
    /// would separate.
    /// </summary>
    private static void AddIndentation(string[] lines, bool[] startsInCode, int[] depths)
    {
        var widths = new int[lines.Length];
        var isCode = new bool[lines.Length];
        for (var row = 0; row < lines.Length; row++)
        {
            if (!startsInCode[row]) continue;
            var width = LeadingWidth(lines[row], out var rest);
            if (rest.Length == 0 || rest[0] == '#') continue;
            widths[row] = width;
            isCode[row] = true;
        }

        var unit = IndentUnit(widths, isCode);

        var level = 0;
        for (var row = lines.Length - 1; row >= 0; row--)
        {
            depths[row] += level;
            if (isCode[row]) level = widths[row] / unit;
        }
    }

    /// <summary>
    /// Width of the leading whitespace, a tab counting as four, and the line
    /// after it.
    /// </summary>
    private static int LeadingWidth(string line, out string rest)
    {
        var width = 0;
        var i = 0;
        for (; i < line.Length; i++)
        {
            if (line[i] == ' ') width++;
            else if (line[i] == '\t') width += 4;
            else break;
        }
        rest = line[i..].TrimEnd();
        return width;
    }

    /// <summary>
    /// How wide one level of indentation is in this file: the largest divisor
    /// of the narrowest indentation that most indented lines are a multiple
    /// of. The narrowest width alone would do, except that a continuation
    /// line aligned under an opening bracket can be narrower than a level and
    /// must not redefine it.
    /// </summary>
    private static int IndentUnit(int[] widths, bool[] isCode)
    {
        var indented = new List<int>();
        for (var row = 0; row < widths.Length; row++)
        {
            if (isCode[row] && widths[row] > 0) indented.Add(widths[row]);
        }
        if (indented.Count == 0) return 4;

        var narrowest = indented.Min();
        for (var unit = narrowest; unit > 1; unit--)
        {
            if (narrowest % unit != 0) continue;
            if (indented.Count(w => w % unit == 0) * 2 > indented.Count) return unit;
        }
        return 1;
    }

    /// <summary>
    /// Index just past the closing quote, or the end of the line when the
    /// literal is not closed on it: an unterminated string is a typo or a
    /// language this scanner does not know, and either way the rest of the
    /// line is not worth counting.
    /// </summary>
    private static int SkipQuoted(string line, int start, char quote, bool backslashEscapes)
    {
        var i = start + 1;
        while (i < line.Length)
        {
            if (backslashEscapes && line[i] == '\\') { i += 2; continue; }
            if (line[i] == quote)
            {
                // VB writes a quote inside a string as two quotes.
                if (!backslashEscapes && i + 1 < line.Length && line[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return line.Length;
    }

    /// <summary>
    /// Whether a slash opens a regular expression rather than divides: it does
    /// after an operator, an opening bracket, a comma, or at the start of the
    /// line. After a name, a number or a closing bracket it is division.
    /// </summary>
    private static bool StartsRegex(string line, int i)
    {
        if (i + 1 < line.Length && (line[i + 1] == '/' || line[i + 1] == '*')) return false;
        var j = i - 1;
        while (j >= 0 && char.IsWhiteSpace(line[j])) j--;
        if (j < 0) return true;
        if ("(,=:[!&|?{};+-*%<>~^".Contains(line[j])) return true;
        var k = j;
        while (k >= 0 && char.IsLetter(line[k])) k--;
        var word = line[(k + 1)..(j + 1)];
        return word is "return" or "typeof" or "case" or "in" or "of";
    }

    /// <summary>Index just past a regular expression and its flags.</summary>
    private static int SkipRegex(string line, int start)
    {
        var i = start + 1;
        var inClass = false;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass)
            {
                i++;
                while (i < line.Length && char.IsLetter(line[i])) i++;
                return i;
            }
            i++;
        }
        return line.Length;
    }

    /// <summary>@"...", $@"..." or @$"...": the quote is escaped by doubling it.</summary>
    private static bool IsVerbatimStart(string line, int i, out int prefix)
    {
        prefix = 0;
        if (line[i] == '@' && i + 1 < line.Length && line[i + 1] == '"') prefix = 2;
        else if (i + 2 < line.Length && line[i + 2] == '"' &&
                 ((line[i] == '$' && line[i + 1] == '@') || (line[i] == '@' && line[i + 1] == '$')))
            prefix = 3;
        return prefix > 0;
    }

    private static int Run(string line, int i, char c)
    {
        var n = 0;
        while (i + n < line.Length && line[i + n] == c) n++;
        return n;
    }

    private static Family FamilyOf(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".py" or ".rb" or ".sh" or ".bash" or ".ps1" or ".psm1" or ".pl" or ".r"
                or ".yml" or ".yaml" or ".toml" => Family.Hash,
            ".vb" or ".vbs" or ".bas" or ".cls" or ".frm" => Family.Basic,
            ".css" => Family.Css,
            _ => Family.CLike,
        };
    }
}
