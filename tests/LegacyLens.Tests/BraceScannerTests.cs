using LegacyLens.Api.Ingestion;

namespace LegacyLens.Tests;

/// <summary>
/// Each case is a brace that is not structure and used to be counted as if it
/// were. The depth after the last line must be what the code really is.
/// </summary>
public class BraceScannerTests
{
    private static int[] Depths(string path, string source) =>
        BraceScanner.Depths(path, source.Split('\n'));

    private static int FinalDepth(string path, string source) => Depths(path, source)[^1];

    [Fact]
    public void Plain_code_counts_every_brace()
    {
        Assert.Equal([1, 2, 1, 0], Depths("A.cs", "class A {\n void M() {\n }\n}"));
    }

    [Fact]
    public void Brace_in_a_string_is_not_structure()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n  Log(\"{0} items\", n);\n}"));
    }

    [Fact]
    public void Escaped_quote_does_not_end_the_string()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n  var s = \"a \\\" {\";\n}"));
    }

    [Fact]
    public void Brace_in_a_char_literal_is_not_structure()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n  if (c == '{') n++;\n}"));
    }

    [Fact]
    public void Brace_in_a_line_comment_is_not_structure()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() { // closes below }\n}"));
    }

    [Fact]
    public void Block_comment_spanning_lines_is_skipped()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n  /* old code:\n  if (x) {\n  */\n}"));
    }

    [Fact]
    public void Verbatim_string_spanning_lines_is_skipped()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n  var sql = @\"SELECT '{'\n  FROM \"\"T\"\" {\";\n}"));
    }

    [Fact]
    public void Interpolated_verbatim_string_is_skipped()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n  var p = $@\"C:\\{dir}\\\";\n}"));
    }

    [Fact]
    public void Raw_string_literal_is_skipped()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n  var json = \"\"\"\n    { \"a\": 1\n    \"\"\";\n}"));
    }

    [Fact]
    public void Javascript_template_literal_is_skipped()
    {
        Assert.Equal(0, FinalDepth("a.js", "function f() {\n  return `{${x}\n  {`;\n}"));
    }

    [Fact]
    public void Python_hash_comment_and_docstring_are_skipped()
    {
        Assert.Equal(0, FinalDepth("a.py", "x = {  # {\n}\n\"\"\"\n{ doc\n\"\"\"\ny = '{'"));
    }

    [Fact]
    public void Visual_basic_apostrophe_starts_a_comment()
    {
        Assert.Equal(0, FinalDepth("A.vb", "Dim s = \"\"\"{\"\" \"   ' a { in a comment"));
    }

    [Fact]
    public void Unterminated_string_ends_with_its_line()
    {
        // A language the scanner does not know may use quotes for something
        // else. Losing the rest of one line is the bounded price.
        Assert.Equal([1, 0], Depths("A.cs", "void M() { var s = \"oops {\n}"));
    }

    [Fact]
    public void Format_string_no_longer_shifts_the_cut()
    {
        // Two methods; the first logs a format string with an opening brace.
        // Counted as structure, that brace kept the blank line between the
        // methods at depth two, so the chunker did not see the seam.
        var first = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"        var v{i} = Read({i});"));
        var source = string.Join('\n',
            "class Report",
            "{",
            "    void Header()",
            "    {",
            "        Log(\"Totals {\");",
            first,
            "    }",
            "",
            "    void Body()",
            "    {",
            first,
            "    }",
            "}");

        var lines = source.Split('\n');
        var blank = Array.IndexOf(lines, "");
        Assert.Equal(1, BraceScanner.Depths("Report.cs", lines)[blank]);
    }

    [Fact]
    public void Preprocessor_directive_is_not_code()
    {
        // Seen in nopCommerce: "#region application/*" opened a comment that
        // swallowed the rest of the file.
        Assert.Equal(0, FinalDepth("MimeTypes.cs", "class M {\n#region application/*\n  const string A = \"a\";\n#endregion\n}"));
    }

    [Fact]
    public void Javascript_regular_expression_holding_a_quote_is_not_a_string()
    {
        Assert.Equal(0, FinalDepth("v.js", "function f(s) {\n  return s.replace(/[^']+/g, '');\n}"));
    }

    [Fact]
    public void Javascript_division_is_not_a_regular_expression()
    {
        Assert.Equal(0, FinalDepth("v.js", "function f(a, b) {\n  return a / b + c / 2;\n}"));
    }

    [Fact]
    public void Css_has_no_line_comments()
    {
        // A base64 image may contain "//"; the closing brace after it is real.
        Assert.Equal(0, FinalDepth("a.css", "li:before{content:url(data:image/png;base64,AB//CD)}\n.x{color:red}"));
    }
}
