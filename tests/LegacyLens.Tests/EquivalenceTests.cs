using LegacyLens.Characterization;

namespace LegacyLens.Tests;

/// <summary>
/// Did the rewrite change what the code does?
///
/// The projection already proves a file compiles and invents nothing. That is a
/// claim about the code being valid, not about it doing the same thing, and the
/// gap between those two is where a migration goes wrong quietly.
///
/// Most of these tests are about what this must refuse to say. A comparison
/// tool that reports "verified" on a file it never managed to call is worse
/// than one that reports nothing, because somebody signs off on the strength of
/// it.
/// </summary>
public class EquivalenceTests
{
    /// <summary>
    /// The configuration a caller actually gets.
    ///
    /// Left at its default on purpose: a smaller budget is not the same tool,
    /// and tests run against a setting nobody uses prove things about a tool
    /// nobody has.
    /// </summary>
    private static EquivalenceReport Compare(string before, string after) =>
        new Equivalence().Compare(before, after);

    private const string Prices = """
        public class Prices
        {
            public int WithTax(int amount) => amount + (amount / 10);
            public string Label(string name) => "Item: " + name;
        }
        """;

    /* ---- when it works ---- */

    [Fact]
    public void An_untouched_file_matches_itself()
    {
        var report = Compare(Prices, Prices);

        Assert.True(report.Ran);
        Assert.True(report.Verified);
        Assert.Equal(2, report.Methods.Count);
        Assert.Empty(report.Moved);
        Assert.True(report.Cases > 0);
    }

    [Fact]
    public void A_rewrite_that_only_moves_the_namespace_is_still_the_same_class()
    {
        // Matching on the full name would report every namespace change as a
        // deletion, which is most of what a framework migration does.
        var before = "namespace Old { public class Prices { public int Double(int n) => n * 2; } }";
        var after = "namespace New { public class Prices { public int Double(int n) => n * 2; } }";

        Assert.True(Compare(before, after).Verified);
    }

    [Fact]
    public void A_changed_result_is_reported_with_the_values_that_produced_it()
    {
        // Without the inputs the reader has a claim and no way to check it.
        var after = Prices.Replace("amount / 10", "amount / 5");

        var report = Compare(Prices, after);

        Assert.False(report.Verified);

        var moved = Assert.Single(report.Moved);
        Assert.Equal("WithTax", moved.Method);

        // 42 rather than a round number: the values are the ones this tool
        // invents, and inventing one for the test would test nothing.
        var divergence = moved.Divergences.First(d => d.Arguments == "42");
        Assert.Equal("46", divergence.Before);
        Assert.Equal("50", divergence.After);
    }

    [Fact]
    public void The_method_that_did_not_move_is_still_reported_as_matched()
    {
        var report = Compare(Prices, Prices.Replace("amount / 10", "amount / 5"));

        Assert.True(report.Methods.Single(m => m.Method == "Label").Matched);
    }

    [Fact]
    public void An_exception_that_appears_is_a_difference_like_any_other()
    {
        // Legacy code throws on inputs its author never expected, and a rewrite
        // that changes which ones is the kind of change nobody notices until a
        // customer does.
        var before = "public class Guard { public int Check(int n) => n; }";
        var after = """
            public class Guard
            {
                public int Check(int n) => n < 0 ? throw new System.ArgumentException("no") : n;
            }
            """;

        var moved = Assert.Single(Compare(before, after).Moved);
        var divergence = moved.Divergences.First(d => d.Arguments == "-1");

        Assert.Equal("-1", divergence.Before);
        Assert.Contains("threw ArgumentException", divergence.After);
    }

    [Fact]
    public void An_exception_that_disappears_is_reported_too()
    {
        var before = """
            public class Guard
            {
                public int Check(int n) => n < 0 ? throw new System.ArgumentException("no") : n;
            }
            """;
        var after = "public class Guard { public int Check(int n) => n; }";

        var moved = Assert.Single(Compare(before, after).Moved);

        Assert.Contains(moved.Divergences, d => d.Before.Contains("threw ArgumentException"));
    }

    [Fact]
    public void An_off_by_one_is_caught_because_the_boundary_was_read_from_the_code()
    {
        // The finding that changed how this works. Invented values are 0, 1,
        // -1, 42 and the two extremes, and none of them separates `>= 3` from
        // `> 3`. The first live run reported this rewrite as unchanged over six
        // calls, which is the exact failure this whole milestone exists to
        // prevent.
        //
        // The boundary was written down three lines away the whole time.
        var before = "public class Loyalty { public int Rate(int years) => years >= 3 ? 10 : 0; }";
        var after = "public class Loyalty { public int Rate(int years) => years > 3 ? 10 : 0; }";

        var moved = Assert.Single(Compare(before, after).Moved);

        var divergence = Assert.Single(moved.Divergences, d => d.Arguments == "3");
        Assert.Equal("10", divergence.Before);
        Assert.Equal("0", divergence.After);
    }

    [Fact]
    public void A_string_the_code_names_is_tried_as_well()
    {
        // The same rule for text: a method that switches on "admin" has said
        // which word matters, and no invented string is ever going to be it.
        var before = """
            public class Roles
            {
                public bool MaySee(string role) => role == "admin" || role == "auditor";
            }
            """;
        var after = """
            public class Roles
            {
                public bool MaySee(string role) => role == "admin";
            }
            """;

        var moved = Assert.Single(Compare(before, after).Moved);

        Assert.Contains(moved.Divergences, d => d.Arguments.Contains("auditor"));
    }

    [Fact]
    public void The_boundary_is_still_tried_when_the_case_budget_is_small()
    {
        // A smaller budget must mean fewer cases, not a different kind of
        // check. Silently dropping the values read from the code would leave
        // the run looking the same and finding less.
        var before = "public class Loyalty { public int Rate(int years) => years >= 3 ? 10 : 0; }";
        var after = "public class Loyalty { public int Rate(int years) => years > 3 ? 10 : 0; }";

        var report = new Equivalence { CasesPerMethod = 4 }.Compare(before, after);

        Assert.Single(report.Moved);
    }

    /* ---- what it must refuse to claim ---- */

    [Fact]
    public void A_file_that_does_not_compile_here_is_said_rather_than_passed()
    {
        // The expected outcome on a real controller: it names System.Web, which
        // is not on this runtime. Claiming anything at all would be the worst
        // thing this could do.
        var before = "using System.Web.Mvc; public class Home : Controller { public int N() => 1; }";

        var report = Compare(before, "public class Home { public int N() => 1; }");

        Assert.False(report.Ran);
        Assert.False(report.Verified);
        Assert.NotEmpty(report.BeforeErrors);
        Assert.Contains("does not compile", report.Claim);
    }

    [Fact]
    public void A_rewrite_that_does_not_compile_is_named_as_the_one_at_fault()
    {
        var report = Compare(Prices, "public class Prices { public int WithTax(int a) => nonsense; }");

        Assert.False(report.Ran);
        Assert.NotEmpty(report.AfterErrors);
        Assert.Empty(report.BeforeErrors);
        Assert.Contains("the rewrite does not compile", report.Claim);
    }

    [Fact]
    public void Nothing_compared_is_not_the_same_as_nothing_moved()
    {
        // The most important line in this file. A file whose work happens
        // through a framework compares nothing at all, and a green tick on that
        // is how a migration gets signed off and discovered in month four.
        var nothing = "public class Empty { public void Go() { } }";

        var report = Compare(nothing, nothing);

        Assert.True(report.Ran);
        Assert.Empty(report.Moved);
        Assert.False(report.Verified);
        Assert.Contains("Nothing was compared", report.Claim);
    }

    [Fact]
    public void A_method_that_disagrees_with_itself_is_dropped_rather_than_reported_as_moved()
    {
        // Without this, every method reading a clock is a behaviour change, and
        // the report becomes noise nobody reads twice.
        var clock = """
            public class Now
            {
                public long Ticks(int ignored) => System.DateTime.Now.Ticks + ignored;
            }
            """;

        var report = Compare(clock, clock);

        Assert.Empty(report.Moved);
        Assert.Contains(report.Skipped, s => s.Reason == SkipReason.NotDeterministic);
    }

    [Fact]
    public void A_rewrite_that_starts_reading_a_clock_is_a_difference_and_not_a_refusal()
    {
        // The other half of the rule above, and the one worth catching: the
        // original was steady, the rewrite is not, and the code now does
        // something it did not do before.
        var before = "public class Stamp { public long At(int n) => n; }";
        var after = "public class Stamp { public long At(int n) => System.DateTime.Now.Ticks + n; }";

        var moved = Assert.Single(Compare(before, after).Moved);

        Assert.Contains(moved.Divergences, d => d.After.Contains("no longer returns the same thing twice"));
    }

    [Fact]
    public void A_method_the_rewrite_dropped_is_a_refusal_rather_than_a_match()
    {
        var after = "public class Prices { public string Label(string name) => \"Item: \" + name; }";

        var report = Compare(Prices, after);

        Assert.True(report.Ran);
        Assert.DoesNotContain(report.Methods, m => m.Method == "WithTax");
        Assert.Contains(report.Skipped, s => s.Reason == SkipReason.SignatureChanged);
    }

    [Fact]
    public void A_class_the_rewrite_dropped_is_named_as_missing()
    {
        var report = Compare(Prices, "public class Other { public int N() => 1; }");

        Assert.True(report.Ran);
        Assert.Empty(report.Methods);
        Assert.Contains(report.Skipped, s => s.Reason == SkipReason.NoCounterpart);
    }

    [Fact]
    public void A_changed_parameter_list_is_a_finding_rather_than_a_comparison()
    {
        // The same name and a different contract is not the same method, and
        // comparing through it would be comparing two different things.
        var after = Prices
            .Replace("int WithTax(int amount)", "long WithTax(long amount)");

        var report = Compare(Prices, after);

        // Asserted first, because a rewrite that failed to compile would make
        // every claim below true for the wrong reason.
        Assert.True(report.Ran);
        Assert.DoesNotContain(report.Methods, m => m.Method == "WithTax");
        Assert.Contains(report.Skipped, s => s.Reason == SkipReason.SignatureChanged);
    }

    [Fact]
    public void A_parameter_the_file_declares_itself_cannot_be_handed_to_both()
    {
        // Recompiled into the other assembly it is a different type with the
        // same name. Rebuilding an equivalent object there would be comparing
        // two objects rather than one.
        // In a namespace on purpose: the value builder refuses to invent
        // objects for types in the global namespace, so a global one would be
        // turned away earlier and this rule would never be reached.
        var source = """
            namespace Shop
            {
                public class Order { public int Quantity { get; set; } }

                public class Totals
                {
                    public int Of(Order order) => order.Quantity * 2;
                }
            }
            """;

        var report = Compare(source, source);

        Assert.True(report.Ran);
        Assert.DoesNotContain(report.Methods, m => m.Method == "Of");
        Assert.Contains(report.Skipped, s => s.Reason == SkipReason.ArgumentNotPortable);
    }

    [Fact]
    public void A_changed_return_type_is_noted_beside_a_result_that_did_not_move()
    {
        // Expected in a framework migration, and not a behaviour change on its
        // own. It is said rather than hidden, and the reader judges.
        var before = "public class Ids { public int Next(int n) => n + 1; }";
        var after = "public class Ids { public long Next(int n) => n + 1; }";

        var report = Compare(before, after);
        var compared = Assert.Single(report.Methods);

        Assert.True(compared.Matched);
        Assert.Contains("Int32", compared.Note);
        Assert.Contains("Int64", compared.Note);
    }

    [Fact]
    public void A_method_whose_every_case_was_dropped_is_not_counted_as_compared()
    {
        // Named but never actually called. Counting it as matched would inflate
        // the only number anybody reads, and a method that is unsteady on both
        // sides is the shortest way there: every case is dropped before
        // anything is compared.
        //
        // Found by mutation. The first version of this test used a rewrite that
        // reads a clock, which produces divergences rather than drops, so the
        // rule it was named after went untested and a mutation of it survived.
        var clock = """
            public class Now
            {
                public long Ticks(int ignored) => System.DateTime.Now.Ticks + ignored;
            }
            """;

        var report = Compare(clock, clock);

        Assert.True(report.Ran);
        Assert.Empty(report.Methods);
        Assert.False(report.Verified);
        Assert.Contains(report.Skipped, s => s.Reason == SkipReason.NothingLeftToCompare);

        // One method, however many of its calls said so. A refusal is recorded
        // per call, and counting entries made a file with a single unsteady
        // method announce that fourteen methods were passed over.
        Assert.Equal(1, report.PassedOver);
        Assert.Contains("1 method(s) were passed over", report.Claim);
        Assert.All(report.Refusals, r => Assert.Equal(1, r.Count));
    }

    [Fact]
    public void A_method_past_the_limit_is_counted_rather_than_forgotten()
    {
        // The number of methods compared must never read as the number of
        // methods there were.
        var source = """
            public class Many
            {
                public int A(int n) => n;
                public int B(int n) => n;
                public int C(int n) => n;
            }
            """;

        var report = new Equivalence { MethodLimit = 1 }.Compare(source, source);

        Assert.Single(report.Methods);
        Assert.Equal(2, report.Skipped.Count(s => s.Reason == SkipReason.BeyondTheLimit));
    }

    [Fact]
    public void A_run_that_runs_out_of_time_stops_and_says_what_it_did_not_reach()
    {
        // The per-call timeout does not bound a whole run: two hundred methods
        // at fourteen cases each, every call spending its full two seconds
        // before being abandoned, is hours. A partial answer with its own
        // limits printed beside it beats a request that never returns.
        var source = """
            public class Many
            {
                public int A(int n) => n;
                public int B(int n) => n;
                public int C(int n) => n;
            }
            """;

        var report = new Equivalence { Budget = TimeSpan.Zero }.Compare(source, source);

        Assert.True(report.Ran);
        Assert.Empty(report.Methods);
        Assert.False(report.Verified);
        Assert.Equal(3, report.Skipped.Count(s => s.Reason == SkipReason.BeyondTheLimit));
    }

    [Fact]
    public void The_claim_never_says_more_than_the_run_did()
    {
        var report = Compare(Prices, Prices);

        Assert.Contains("2 method(s)", report.Claim);
        Assert.Contains("Nothing else in the file was checked", report.Claim);
    }

    [Fact]
    public void Only_a_handful_of_divergent_calls_are_kept_per_method()
    {
        // Enough to diagnose, not enough to scroll. A method that changed for
        // every input would otherwise print its whole case list.
        var before = "public class All { public int N(int n) => n; }";
        var after = "public class All { public int N(int n) => n + 1; }";

        var moved = Assert.Single(
            new Equivalence { CasesPerMethod = 50 }.Compare(before, after).Moved);

        Assert.True(moved.Cases > 5);
        Assert.Equal(5, moved.Divergences.Count);
    }

    [Fact]
    public void Static_methods_are_compared_like_any_other()
    {
        var before = "public class Maths { public static int Twice(int n) => n * 2; }";
        var after = "public class Maths { public static int Twice(int n) => n + n; }";

        Assert.True(Compare(before, after).Verified);
    }

    [Fact]
    public void Two_runs_over_the_same_pair_say_the_same_thing()
    {
        // The values are fixed rather than random, so a report is reviewable
        // and a second opinion is the same opinion.
        var after = Prices.Replace("amount / 10", "amount / 5");

        var first = Compare(Prices, after);
        var second = Compare(Prices, after);

        Assert.Equal(
            first.Moved.Single().Divergences.Select(d => d.Arguments),
            second.Moved.Single().Divergences.Select(d => d.Arguments));
    }
}
