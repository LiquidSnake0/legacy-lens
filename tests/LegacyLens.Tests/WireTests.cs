using System.Text.Json;
using LegacyLens.Characterization;

namespace LegacyLens.Tests;

/// <summary>
/// What crosses the process boundary, and what is recomputed on arrival.
///
/// The split is the point. Facts travel; the sentences read off them do not.
/// A claim sent as text could arrive beside numbers that no longer support it,
/// and the one sentence in this tool that must never be wrong is the one that
/// says nothing moved.
/// </summary>
public class WireTests
{
    private static EquivalenceReport Report() => new(
        Ran: true,
        BeforeErrors: [],
        AfterErrors: [],
        Methods:
        [
            new Compared("Pricing", "WithTax", "WithTax(Int32)", 12,
                [new Divergence("100", "110", "100")]),
            new Compared("Pricing", "Label", "Label(String)", 6, [], Note: "returns a string now"),
        ],
        Skipped:
        [
            new Skipped("Pricing.Save", SkipReason.NothingToObserve),
            new Skipped("Pricing.Now", SkipReason.NotDeterministic, "a clock"),
        ],
        ElapsedMs: 1127);

    [Fact]
    public void A_report_survives_the_trip_unchanged()
    {
        var arrived = Wire.Read(Wire.Write(Report()));

        Assert.NotNull(arrived);
        Assert.True(arrived.Ran);
        Assert.Equal(18, arrived.Cases);
        Assert.Equal(1, arrived.Moved.Count);
        Assert.Equal("100", arrived.Moved[0].Divergences[0].Arguments);
        Assert.Equal("110", arrived.Moved[0].Divergences[0].Before);
        Assert.Equal("returns a string now", arrived.Methods[1].Note);
        Assert.Equal(1127, arrived.ElapsedMs);
        Assert.Equal(2, arrived.PassedOver);
    }

    [Fact]
    public void The_sentences_are_read_off_the_facts_on_arrival_rather_than_sent()
    {
        var written = Wire.Write(Report());

        Assert.DoesNotContain("\"Claim\"", written);
        Assert.DoesNotContain("\"Verified\"", written);
        Assert.DoesNotContain("\"Matched\"", written);
        Assert.DoesNotContain("\"Refusals\"", written);
        Assert.DoesNotContain("\"Moved\"", written);

        // And they still come out right, because they are computed from what
        // did arrive.
        var arrived = Wire.Read(written)!;

        Assert.Equal(Report().Claim, arrived.Claim);
        Assert.False(arrived.Verified);
        Assert.False(arrived.Methods[0].Matched);
        Assert.True(arrived.Methods[1].Matched);
    }

    [Fact]
    public void A_reason_travels_as_its_name_and_not_as_its_number()
    {
        // Inserting a value into the middle of SkipReason would otherwise
        // silently relabel every refusal a slightly older child reported.
        var written = Wire.Write(Report());

        Assert.Contains("NotDeterministic", written);

        Assert.Equal(SkipReason.NotDeterministic, Wire.Read(written)!.Skipped[1].Reason);
    }

    [Fact]
    public void An_interruption_arrives_as_the_claim_and_outranks_every_other_reading()
    {
        var stopped = new EquivalenceReport(false, [], [], [], [], 40, "Nothing was checked: it was stopped.");

        var arrived = Wire.Read(Wire.Write(stopped))!;

        Assert.Equal("Nothing was checked: it was stopped.", arrived.Claim);
        Assert.False(arrived.Verified);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"Ran\": ")]
    public void What_is_not_a_report_is_not_read_as_one(string arrived)
    {
        // Never an exception and never an empty pass. The caller decides what
        // to say about nothing, and it is never "nothing moved".
        Assert.Null(Wire.Read(arrived));
    }

    [Fact]
    public void A_report_with_nothing_in_it_still_says_so_rather_than_passing()
    {
        var empty = new EquivalenceReport(true, [], [], [], [], 12);

        var arrived = Wire.Read(Wire.Write(empty))!;

        Assert.False(arrived.Verified);
        Assert.Contains("Nothing was compared", arrived.Claim);
    }

    [Fact]
    public void The_format_is_the_one_the_command_actually_prints()
    {
        // Read straight rather than through Wire, so that a change to the
        // options on one side is a failing test rather than a child that talks
        // to nobody.
        using var read = JsonDocument.Parse(Wire.Write(Report()));

        Assert.True(read.RootElement.TryGetProperty("Methods", out var methods));
        Assert.Equal(2, methods.GetArrayLength());
        Assert.True(read.RootElement.TryGetProperty("Skipped", out _));
        Assert.True(read.RootElement.TryGetProperty("ElapsedMs", out _));
    }
}

/// <summary>
/// What a dying child says, as the report repeats it.
///
/// Split out because it was wrong in the first working version and only a real
/// run showed it: the tail of a crashing runtime's output is a stack frame from
/// the middle of a recursion, cut mid-word.
/// </summary>
public class ComplaintTests
{
    [Fact]
    public void The_first_line_is_what_gets_repeated_and_not_the_last()
    {
        var overflow = """
            Stack overflow.
               at Deep.Recurse(Int32)
               at Deep.Recurse(Int32)
               at System.Threading.Tasks.Task.ExecuteWithThreadLocal(Task ByRef, Thread)
            """;

        Assert.Equal("Stack overflow.", Detached.Opening(overflow));
    }

    [Fact]
    public void Leading_blank_lines_are_not_the_message()
    {
        Assert.Equal("Unhandled exception. System.Exception: no", Detached.Opening(
            "\n\n   Unhandled exception. System.Exception: no\n   at Thing.Go()"));
    }

    [Fact]
    public void A_line_too_long_to_print_is_cut_where_a_reader_can_see_it_was_cut()
    {
        var reported = Detached.Opening(new string('x', 500));

        Assert.EndsWith("...", reported);
        Assert.Equal(203, reported.Length);
    }

    [Fact]
    public void Nothing_said_is_not_a_message()
    {
        Assert.Equal(string.Empty, Detached.Opening("   \n \n "));
    }
}
