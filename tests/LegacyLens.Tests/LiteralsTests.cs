using LegacyLens.Characterization;

namespace LegacyLens.Tests;

/// <summary>
/// The values a file mentions, offered back to it as arguments.
///
/// Invented values find the boundaries somebody thought of in advance: empty,
/// zero, negative, the extremes. They do not find the boundary this particular
/// code turns on, and that boundary is written down a few lines away.
/// </summary>
public class LiteralsTests
{
    private static IReadOnlyList<object?> Of<T>(string source)
    {
        var found = Literals.From(source);
        return found.TryGetValue(typeof(T), out var values) ? values : [];
    }

    [Fact]
    public void A_number_in_the_source_comes_back_with_the_two_either_side_of_it()
    {
        // An off-by-one lives beside a constant rather than on it: a rewrite
        // turning `>= 3` into `> 3` still agrees at 3, and it is 2 and 4 that
        // settle the question.
        var found = Of<int>("public class A { public bool Big(int n) => n >= 3; }");

        Assert.Contains(3, found);
        Assert.Contains(2, found);
        Assert.Contains(4, found);
    }

    [Fact]
    public void Numbers_are_ranked_by_size_rather_than_by_how_they_print()
    {
        // The bug this test exists for. Sorted as text, "1000" comes before
        // "7", so a file with several configuration numbers and one boundary
        // kept the configuration and dropped the boundary. Found by running it
        // on a file with both.
        //
        // Enough numbers to overflow the cap on purpose: with only a few, both
        // orders keep everything and the test proves nothing. Found by
        // mutation, which is how the first version of it was caught.
        var source = """
            public class Paging
            {
                public int Take(int page)
                {
                    if (page > 7) return 1000;
                    if (page > 3) return 2000;
                    return 3000;
                }
            }
            """;

        var found = Of<int>(source);

        Assert.Contains(7, found);
        Assert.Contains(8, found);
        Assert.DoesNotContain(3000, found);
    }

    [Fact]
    public void A_value_the_file_repeats_comes_before_one_it_mentions_once()
    {
        // The value and its neighbours are ranked together, because a neighbour
        // is only there on account of the value: 5 mentioned three times brings
        // 4 and 6 with it three times, and all three outrank a number the file
        // says once.
        var source = """
            public class Retry
            {
                public int Times(int n) => n == 5 ? 5 : (n > 5 ? 5 : 900);
            }
            """;

        var found = Of<int>(source);

        Assert.True(found.Take(3).Order().SequenceEqual(new[] { 4, 5, 6 }.Cast<object?>()));
        Assert.True(found.ToList().IndexOf(900) > found.ToList().IndexOf(5));
    }

    [Fact]
    public void Only_a_handful_of_each_kind_is_kept()
    {
        // A file is not a corpus, and every extra value is four more calls into
        // code nobody has read.
        var numbers = string.Join(" + ", Enumerable.Range(100, 40));
        var found = Of<int>($"public class Big {{ public int N() => {numbers}; }}");

        Assert.True(found.Count <= 8);
    }

    [Fact]
    public void Strings_come_back_and_the_long_ones_do_not()
    {
        // A short string is a boundary. A long one is prose, SQL or a message,
        // and passing it as an argument tests nothing.
        var prose = new string('x', 60);
        var source = $"public class A {{ public string N() => \"INV-\" + \"{prose}\"; }}";

        var found = Of<string>(source);

        Assert.Contains("INV-", found);
        Assert.DoesNotContain(prose, found);
    }

    [Fact]
    public void A_source_that_will_not_parse_gives_nothing_rather_than_throwing()
    {
        // The caller is about to compile it and will report that properly.
        Assert.Empty(Literals.From("public class Broken {"));
    }

    [Fact]
    public void A_file_with_no_literals_offers_none()
    {
        Assert.Empty(Literals.From("public class A { public int N(int x) => x; }"));
    }

    [Fact]
    public void The_values_reach_the_cases_that_are_actually_tried()
    {
        // The end of the chain, and the only part that matters to a reader:
        // the number in the file is passed to the method.
        var cases = Values.Cases(
            [typeof(int)], 20,
            type => Literals.From("public class A { public bool B(int n) => n >= 7; }")
                .TryGetValue(type, out var values) ? values : []);

        Assert.Contains(cases, c => Equals(c[0], 7));
        Assert.Contains(cases, c => Equals(c[0], 6));

        // The invented boundaries are still there, and still first.
        Assert.Equal(0, cases[0][0]);
    }

    [Fact]
    public void The_values_still_get_a_turn_when_the_case_budget_is_small()
    {
        // The limit caps rows, not candidates. Appended after the invented
        // ones, the file's values sat past the end of any short run: lowering
        // the budget switched off reading the code without saying so. Taken in
        // turn, both kinds get a go at every budget.
        var cases = Values.Cases(
            [typeof(int)], 4,
            type => Literals.From("public class A { public bool B(int n) => n >= 7; }")
                .TryGetValue(type, out var values) ? values : []);

        Assert.Equal(4, cases.Count);
        Assert.Contains(cases, c => Equals(c[0], 6));
        Assert.Equal(0, cases[0][0]);
    }

    [Fact]
    public void A_value_the_tool_already_invents_is_not_added_twice()
    {
        var cases = Values.Cases(
            [typeof(int)], 20,
            type => Literals.From("public class A { public bool B(int n) => n >= 0; }")
                .TryGetValue(type, out var values) ? values : []);

        Assert.Equal(1, cases.Count(c => Equals(c[0], 0)));
    }
}
