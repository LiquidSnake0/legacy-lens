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

    /// <summary>
    /// What is open at the end of a line and does not end with it: a
    /// multi-line string, or a hole in an interpolated string where code is
    /// being read again. The frames stack: a hole may hold a string that
    /// holds another hole.
    /// </summary>
    private sealed class Frame
    {
        /// <summary>Delimiter that closes the string; null for a hole.</summary>
        public string? Closing;
        public bool Escapes;
        public bool DoubledQuote;
        /// <summary>Braces that open a hole in this string; 0 when it has no holes.</summary>
        public int HoleBraces;
        /// <summary>Character before the brace that opens a hole: '$' in JavaScript, none in C#.</summary>
        public char HolePrefix = '\0';
        /// <summary>Unmatched braces of the code inside a hole.</summary>
        public int Depth;
        /// <summary>Whether this survives the end of the line.</summary>
        public bool MultiLine;

        public bool IsHole => Closing is null;
    }

    public static int[] Depths(string filePath, string[] lines)
    {
        var family = FamilyOf(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var csharp = extension == ".cs";
        var preprocessed = extension is ".cs" or ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hpp" or ".m";
        var javascript = extension is ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx";
        var razor = extension is ".cshtml" or ".razor";

        var depths = new int[lines.Length];
        var depth = 0;

        // Whether a line starts in code rather than inside a multi-line string
        // or comment; only those lines say anything about indentation.
        var startsInCode = new bool[lines.Length];

        var inBlockComment = false;
        // What closes the open block comment: */ almost everywhere, *@ in Razor.
        var commentClose = "*/";
        var frames = new Stack<Frame>();

        for (var row = 0; row < lines.Length; row++)
        {
            var line = lines[row];
            var i = 0;
            startsInCode[row] = !inBlockComment && frames.Count == 0;

            // #region application/* is a directive, not code: its /* would
            // open a comment that never closes.
            if (preprocessed && !inBlockComment && frames.Count == 0 && line.TrimStart().StartsWith('#'))
            {
                depths[row] = depth;
                continue;
            }

            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    var end = line.IndexOf(commentClose, i, StringComparison.Ordinal);
                    if (end < 0) { i = line.Length; break; }
                    inBlockComment = false;
                    i = end + commentClose.Length;
                    continue;
                }

                var top = frames.Count > 0 ? frames.Peek() : null;

                if (top is { IsHole: false })
                {
                    // Inside a string: look for its end, an escape, or a hole.
                    if (top.Escapes && line[i] == '\\') { i += 2; continue; }
                    if (top.DoubledQuote && line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    var consumed = 0;
                    if (top.HoleBraces > 0 && OpensHole(line, i, top, out consumed))
                    {
                        i += consumed;
                        frames.Push(new Frame { MultiLine = top.MultiLine });
                        continue;
                    }
                    if (consumed > 0) { i += consumed; continue; }
                    if (string.CompareOrdinal(line, i, top.Closing, 0, top.Closing!.Length) == 0)
                    {
                        i += top.Closing.Length;
                        frames.Pop();
                        continue;
                    }
                    i++;
                    continue;
                }

                var c = line[i];

                // Razor: an @ switches to C# for the expression that follows
                // it, wherever it sits, including inside a quoted string of
                // the markup or of a script block. The expression's own
                // strings and character literals are read by the C# rules,
                // so a '"' inside it neither ends the surrounding string nor
                // starts one. The one view under repos/ that would not
                // balance held exactly that: a JavaScript string with
                // @T("...").EncodeJsString('"', false) inside.
                if (razor && c == '@' && i + 1 < line.Length)
                {
                    var next = line[i + 1];
                    if (next == '*') { inBlockComment = true; commentClose = "*@"; i += 2; continue; }
                    if (next == '@') { i += 2; continue; }
                    if (next == '(' || char.IsLetter(next) || next == '_')
                    {
                        i = SkipRazorExpression(line, i);
                        continue;
                    }
                }

                // Comments.
                if ((family == Family.CLike || family == Family.Css) && c == '/' && i + 1 < line.Length)
                {
                    // CSS has no line comments: a base64 image may contain //.
                    if (line[i + 1] == '/' && family == Family.CLike) break;
                    if (line[i + 1] == '*') { inBlockComment = true; commentClose = "*/"; i += 2; continue; }
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

                // Strings.
                if (csharp && StartsCSharpString(line, i, out var frame, out var prefix))
                {
                    // Whatever sits inside a hole is code again: braces count,
                    // and a quote inside it does not end the string.
                    frames.Push(frame);
                    i += prefix;
                    continue;
                }
                var quotes = Run(line, i, '"');
                if (quotes >= 3 && family == Family.Hash)
                {
                    // A Python docstring: closed by the same run of quotes.
                    frames.Push(new Frame { Closing = new string('"', quotes), Escapes = true, MultiLine = true });
                    i += quotes;
                    continue;
                }
                if (family == Family.Hash && Run(line, i, '\'') >= 3)
                {
                    frames.Push(new Frame { Closing = "'''", Escapes = true, MultiLine = true });
                    i += 3;
                    continue;
                }
                if (javascript && c == '`')
                {
                    // JavaScript template literal. Its ${...} holes are code,
                    // and that code may hold another template literal: read
                    // as one string up to the next backtick, a nested
                    // template ended the outer one early and the braces after
                    // it were counted. Seen in both Vue bundles under repos/.
                    frames.Push(new Frame
                    {
                        Closing = "`", Escapes = true, MultiLine = true,
                        HoleBraces = 1, HolePrefix = '$',
                    });
                    i++;
                    continue;
                }

                // Strings and character literals that end with their line.
                if (c == '"' || (c == '\'' && family != Family.Basic))
                {
                    i = SkipQuoted(line, i, c, backslashEscapes: family != Family.Basic, razor);
                    continue;
                }

                if (c == '{')
                {
                    if (top is not null) top.Depth++; else depth++;
                }
                else if (c == '}')
                {
                    if (top is not null)
                    {
                        if (top.Depth > 0) top.Depth--;
                        else
                        {
                            // The hole closes; its string is the frame below.
                            frames.Pop();
                            i += frames.Peek().HoleBraces;
                            continue;
                        }
                    }
                    else depth--;
                }
                i++;
            }

            // A string that ends with its line takes any hole open inside it
            // down with it.
            while (frames.Count > 0 && !frames.Peek().MultiLine) frames.Pop();

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
    /// Whether an opening brace at <paramref name="i"/> opens a hole in the
    /// interpolated string <paramref name="s"/>. In $"..." a single brace
    /// opens a hole and "{{" is a literal brace; in $$"""...""" two braces
    /// open a hole and one is literal; in a JavaScript template literal the
    /// brace must follow a dollar sign. <paramref name="consumed"/> is how
    /// many characters were recognised either way, so a literal doubled
    /// brace is not read twice.
    /// </summary>
    private static bool OpensHole(string line, int i, Frame s, out int consumed)
    {
        consumed = 0;
        if (s.HolePrefix != '\0')
        {
            if (line[i] != s.HolePrefix || i + 1 >= line.Length || line[i + 1] != '{') return false;
            consumed = 2;
            return true;
        }
        if (line[i] != '{') return false;
        var run = Run(line, i, '{');
        if (run >= 2 * s.HoleBraces)
        {
            // Literal braces, written doubled.
            consumed = 2 * s.HoleBraces;
            return false;
        }
        if (run < s.HoleBraces) { consumed = run; return false; }
        consumed = s.HoleBraces;
        return true;
    }

    /// <summary>
    /// A C# string that is not a plain "..." one: verbatim, raw, interpolated,
    /// or a combination. Those are the ones whose rules SkipQuoted does not
    /// know. Plain strings and character literals stay with SkipQuoted.
    /// </summary>
    private static bool StartsCSharpString(string line, int i, out Frame frame, out int prefix)
    {
        frame = null!;
        prefix = 0;
        var dollars = Run(line, i, '$');
        var j = i + dollars;
        var verbatim = j < line.Length && line[j] == '@';
        if (verbatim) j++;
        if (dollars == 0 && verbatim && j < line.Length && line[j] == '$')
        {
            // @$"..." is the same string as $@"...".
            dollars = Run(line, j, '$');
            j += dollars;
        }
        if (j >= line.Length || line[j] != '"') return false;

        var quotes = Run(line, j, '"');
        if (quotes >= 3 && !verbatim)
        {
            // Raw: closed by the same run of quotes, escapes not interpreted,
            // holes opened by as many braces as there were dollar signs.
            frame = new Frame
            {
                Closing = new string('"', quotes), MultiLine = true,
                HoleBraces = dollars,
            };
            prefix = j + quotes - i;
            return true;
        }
        if (!verbatim && dollars == 0) return false;

        frame = new Frame
        {
            Closing = "\"",
            Escapes = !verbatim,
            DoubledQuote = verbatim,
            MultiLine = verbatim,
            HoleBraces = dollars > 0 ? 1 : 0,
        };
        prefix = j + 1 - i;
        return true;
    }


    /// <summary>
    /// Index just past the closing quote, or the end of the line when the
    /// literal is not closed on it: an unterminated string is a typo or a
    /// language this scanner does not know, and either way the rest of the
    /// line is not worth counting.
    /// </summary>
    private static int SkipQuoted(string line, int start, char quote, bool backslashEscapes, bool razor = false)
    {
        var i = start + 1;
        while (i < line.Length)
        {
            if (backslashEscapes && line[i] == '\\') { i += 2; continue; }
            if (razor && line[i] == '@' && i + 1 < line.Length
                && (line[i + 1] == '(' || char.IsLetter(line[i + 1]) || line[i + 1] == '_'))
            {
                // C# inside the string: its quotes are its own.
                i = SkipRazorExpression(line, i);
                continue;
            }
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
    /// Index just past a Razor expression starting at the @ at
    /// <paramref name="at"/>: @(...) up to its closing parenthesis, or a name
    /// followed by any run of .member, (arguments) and [index]. The
    /// parentheses and brackets are balanced with C# strings and character
    /// literals read as such, and braces inside them are arguments, not
    /// structure. The expression ends with its line: a call continued on the
    /// next line is read as ordinary code from there.
    ///
    /// A keyword such as @if or @foreach stops at the space after it, so the
    /// block it opens is counted by the ordinary rules; @{ is not an
    /// expression and its braces count the same way.
    /// </summary>
    private static int SkipRazorExpression(string line, int at)
    {
        var i = at + 1;
        if (i < line.Length && line[i] == '(') return SkipBalanced(line, i, '(', ')');

        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
        while (i < line.Length)
        {
            if (line[i] == '.' && i + 1 < line.Length && (char.IsLetter(line[i + 1]) || line[i + 1] == '_'))
            {
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                continue;
            }
            if (line[i] == '(') { i = SkipBalanced(line, i, '(', ')'); continue; }
            if (line[i] == '[') { i = SkipBalanced(line, i, '[', ']'); continue; }
            break;
        }
        return i;
    }

    /// <summary>
    /// Index just past the bracket matching the one at <paramref name="open"/>,
    /// reading C# strings, verbatim strings and character literals on the way
    /// so that a bracket inside one of them does not count. The end of the
    /// line when nothing matches.
    /// </summary>
    private static int SkipBalanced(string line, int open, char opening, char closing)
    {
        var depth = 0;
        var i = open;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                i = SkipQuoted(line, i + 1, '"', backslashEscapes: false);
                continue;
            }
            if (c == '"' || c == '\'')
            {
                i = SkipQuoted(line, i, c, backslashEscapes: true);
                continue;
            }
            if (c == opening) depth++;
            else if (c == closing && --depth == 0) return i + 1;
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
