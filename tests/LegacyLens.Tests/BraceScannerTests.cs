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

    // ---- holes in interpolated strings --------------------------------------

    [Fact]
    public void Quote_inside_an_interpolation_hole_does_not_end_the_string()
    {
        // Read as a plain string, the quote in the hole ended it early and the
        // rest of the line, the block that follows included, was lost.
        var source = string.Join('\n',
            "void M() {",
            "    var s = $\"{(flag ? \"on\" : \"off\")}\"; if (s.Length > 0) {",
            "    }",
            "}");
        Assert.Equal([1, 2, 1, 0], Depths("A.cs", source));
    }

    [Fact]
    public void Braces_inside_a_hole_are_code_and_balance()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n    var s = $\"{new { A = 1 }.A} {d[\"k\"]}\";\n}"));
    }

    [Fact]
    public void Doubled_braces_in_an_interpolated_string_are_literal()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n    var s = $\"{{not a hole}} {x}\";\n}"));
    }

    [Fact]
    public void Interpolated_strings_nest()
    {
        Assert.Equal(0, FinalDepth("A.cs", "void M() {\n    var s = $\"{(a ? $\"{b}\" : \"c\")}\";\n}"));
    }

    [Fact]
    public void Verbatim_interpolated_string_keeps_its_holes_and_doubled_quotes()
    {
        var source = string.Join('\n',
            "void M() {",
            "    var s = $@\"\"\"{Path.Combine(\"a\", \"b\")}\"\"",
            "      {{literal}} {x}\";",
            "}");
        Assert.Equal(0, FinalDepth("A.cs", source));
    }

    [Fact]
    public void Raw_interpolated_string_opens_holes_with_as_many_braces_as_dollars()
    {
        // With two dollar signs a single brace is literal JSON and a pair
        // opens a hole; the hole holds a string with a brace of its own.
        var source = string.Join('\n',
            "void M() {",
            "    var json = $$\"\"\"",
            "        { \"count\": {{Count(\"}\")}} }",
            "        \"\"\";",
            "}");
        Assert.Equal([1, 1, 1, 1, 0], Depths("A.cs", source));
    }

    [Fact]
    public void Nested_template_literals_do_not_end_each_other()
    {
        // From vue.global.js: a template literal whose hole holds two more.
        // Read as a plain string up to the next backtick, the outer one ended
        // at `$event` and the `{` in the last piece was counted as code.
        var source = string.Join('\n',
            "function f(isInline, multi) {",
            "  return [`${isInline ? `$event` : `${``}(...args)`} => ${multi ? `{` : `(`}`, exp];",
            "}");
        Assert.Equal([1, 1, 0], Depths("v.js", source));
    }

    [Fact]
    public void Template_literal_hole_spanning_lines_is_still_code()
    {
        var source = string.Join('\n',
            "const s = `a ${",
            "  items.map(i => { return i; }).join(\"`\")",
            "} b`;",
            "function g() {",
            "}");
        Assert.Equal([0, 0, 0, 1, 0], Depths("v.js", source));
    }

    [Fact]
    public void Escaped_dollar_brace_in_a_template_literal_is_not_a_hole()
    {
        Assert.Equal(0, FinalDepth("v.js", "function f() {\n  return `\\${not} {`;\n}"));
    }

    // ---- indentation, for languages that have no braces ------------------

    [Fact]
    public void Python_depth_is_the_indentation_of_the_code_that_follows()
    {
        // The blank line between the two functions is at depth zero: a cut
        // there separates two top-level definitions. The line before it is
        // too, because what follows it is top-level code.
        Assert.Equal([1, 0, 0, 1, 0],
            Depths("a.py", "def a():\n    x = 1\n\ndef b():\n    y = 2"));
    }

    [Fact]
    public void Python_blank_line_inside_a_class_is_one_level_deep()
    {
        var source = string.Join('\n',
            "class A:",
            "    def m(self):",
            "        pass",
            "",
            "    def n(self):",
            "        pass");
        Assert.Equal([1, 2, 1, 1, 2, 0], Depths("a.py", source));
    }

    [Fact]
    public void Python_docstring_and_comment_lines_take_the_level_of_the_code_after_them()
    {
        var source = string.Join('\n',
            "def a():",
            "    \"\"\"",
            "    not code: {",
            "    \"\"\"",
            "    # neither is this",
            "    return 1");
        Assert.Equal([1, 1, 1, 1, 1, 0], Depths("a.py", source));
    }

    [Fact]
    public void Indentation_unit_is_read_from_the_file()
    {
        // Two-space YAML and tab-indented Python both count one level per step.
        Assert.Equal([1, 2, 2, 0], Depths("a.yml", "a:\n  b:\n    c: 1\n    d: 2"));
        Assert.Equal([1, 1, 0], Depths("a.py", "def a():\n\tx = 1\n\treturn x"));
    }

    [Fact]
    public void A_continuation_line_does_not_redefine_the_unit()
    {
        // The argument aligned under the bracket sits at width 10; the unit
        // stays four because four is the width that repeats.
        var source = string.Join('\n',
            "def a():",
            "    call(one,",
            "          two)",
            "    return 1",
            "",
            "def b():",
            "    pass");
        var depths = Depths("a.py", source);
        Assert.Equal(0, depths[4]);
        Assert.Equal(1, depths[0]);
    }

    [Fact]
    public void Braces_and_indentation_add_up_in_python()
    {
        // A blank line inside a dictionary literal is not a seam, even at the
        // top level: the open brace still counts.
        Assert.Equal([1, 1, 0], Depths("a.py", "x = {\n\n}"));
    }

    // Razor: @ switches to C# for the expression that follows, wherever it is.

    [Fact]
    public void Razor_expression_inside_a_script_string_keeps_its_quotes_to_itself()
    {
        // The one view under repos/ that did not balance: a JavaScript string
        // holding a C# call with a '"' character literal.
        Assert.Equal(0, FinalDepth("Confirm.cshtml",
            "<script>\n"
            + "  $.throbber.show({ white: true, message: '@T(\"Checkout.OrderCompletes\").ToString().EncodeJsString('\"', false)' });\n"
            + "</script>"));
    }

    [Fact]
    public void Razor_code_block_counts_its_braces()
    {
        Assert.Equal([1, 1, 0, 0], Depths("View.cshtml",
            "@{\n  var terms = T(\"{0}\");\n}\n<p>@terms</p>"));
    }

    [Fact]
    public void Razor_explicit_expression_in_an_attribute_is_not_structure()
    {
        Assert.Equal(0, FinalDepth("View.cshtml",
            "<input class=\"a\" @(Model.On ? \"checked='checked' {\" : \"\") />"));
    }

    [Fact]
    public void Razor_comment_spanning_lines_is_skipped()
    {
        Assert.Equal(0, FinalDepth("View.cshtml", "@* draft:\n@if (x) {\n*@\n<p>done</p>"));
    }

    [Fact]
    public void Razor_keyword_block_is_counted_and_its_strings_are_not()
    {
        Assert.Equal([0, 1, 0], Depths("View.cshtml",
            "@if (Model.Name == \"}\")\n{\n}"));
    }

    [Fact]
    public void Razor_implicit_expression_with_arguments_holding_braces_is_not_structure()
    {
        Assert.Equal(0, FinalDepth("View.cshtml",
            "@Html.RenderAction(\"X\", new { step = 1 })\n@Model.Items[0].Name"));
    }

    [Fact]
    public void Doubled_at_is_a_literal_at()
    {
        Assert.Equal(0, FinalDepth("View.cshtml", "<p>mail me @@home</p>"));
    }
}
