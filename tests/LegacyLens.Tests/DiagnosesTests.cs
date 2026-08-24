using LegacyLens.Analysis;
using LegacyLens.Api.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace LegacyLens.Tests;

/// <summary>
/// Answers kept, and nothing else.
///
/// The state of a diagnosis is a fold over its answers, so the store holds only
/// the answers. The tests here are about what happens when that assumption is
/// pushed: an answer changed halfway through, a workspace deleted, two projects
/// answering the same question differently.
/// </summary>
public class DiagnosesTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"lens-diagnoses-{Guid.NewGuid():N}.db");

    private SqliteVectorStore Store()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["INDEX_PATH"] = _path })
            .Build();
        return new SqliteVectorStore(config);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Two outcomes, one question, which is enough to see the fold work.</summary>
    private static Dilemma Sticky() => new(
        "session-state", "Where state goes", ["HttpSessionState"], "what it is",
        [
            new Outcome("distributed", "Move it out of process", ""),
            new Outcome("sticky", "Pin the sessions", ""),
        ],
        [
            new Question("machines", "How many machines?", "why",
            [
                new Choice("one", ["distributed"], "one machine cannot lose it"),
                new Choice("several", ["sticky"], "pinning breaks when one dies"),
            ]),
        ]);

    [Fact]
    public void An_answer_survives_the_process_that_recorded_it()
    {
        using var store = Store();
        new Diagnoses(store.Connection).Answer("alpha", "session-state", "machines", "several");

        var answers = new Diagnoses(store.Connection).Answers("alpha", "session-state");

        Assert.Equal("machines", Assert.Single(answers).QuestionId);
        Assert.Equal("several", answers[0].Answer);
    }

    [Fact]
    public void Changing_an_answer_replaces_it_rather_than_holding_both()
    {
        // Both stored is a diagnosis that contradicts itself, and the fold
        // would rule out every outcome and land on nothing.
        using var store = Store();
        var diagnoses = new Diagnoses(store.Connection);

        diagnoses.Answer("alpha", "session-state", "machines", "one");
        diagnoses.Answer("alpha", "session-state", "machines", "several");

        var answers = diagnoses.Answers("alpha", "session-state");

        Assert.Equal("several", Assert.Single(answers).Answer);
        Assert.Equal(["distributed"], new Diagnosis(Sticky(), answers).Remaining.Select(o => o.Id));
    }

    [Fact]
    public void Answers_come_back_in_the_order_they_were_first_given()
    {
        // The reasoning is read back as a sequence, and re-answering something
        // should not shuffle it to the end and reorder somebody's own argument
        // under them.
        using var store = Store();
        var diagnoses = new Diagnoses(store.Connection);

        diagnoses.Answer("alpha", "d", "first", "a");
        diagnoses.Answer("alpha", "d", "second", "b");
        diagnoses.Answer("alpha", "d", "first", "c");

        Assert.Equal(["first", "second"], diagnoses.Answers("alpha", "d").Select(a => a.QuestionId));
    }

    [Fact]
    public void Two_workspaces_answer_the_same_question_differently()
    {
        // Two projects behind two different load balancers give two different
        // answers, and the right one depends on which project is open.
        using var store = Store();
        var diagnoses = new Diagnoses(store.Connection);

        diagnoses.Answer("alpha", "session-state", "machines", "one");
        diagnoses.Answer("beta", "session-state", "machines", "several");

        Assert.Equal("one", diagnoses.Answers("alpha", "session-state")[0].Answer);
        Assert.Equal("several", diagnoses.Answers("beta", "session-state")[0].Answer);
    }

    [Fact]
    public void Forgetting_one_dilemma_leaves_the_others_standing()
    {
        using var store = Store();
        var diagnoses = new Diagnoses(store.Connection);

        diagnoses.Answer("alpha", "session-state", "machines", "one");
        diagnoses.Answer("alpha", "output-cache", "shared", "yes");

        Assert.Equal(1, diagnoses.Forget("alpha", "session-state"));

        Assert.Empty(diagnoses.Answers("alpha", "session-state"));
        Assert.Single(diagnoses.Answers("alpha", "output-cache"));
    }

    [Fact]
    public void Deleting_a_workspace_takes_its_answers_and_not_another_workspaces()
    {
        // Left behind, they would be handed to whichever workspace later reuses
        // the identifier, which is how somebody else's load balancer ends up
        // deciding your migration.
        using var store = Store();
        var diagnoses = new Diagnoses(store.Connection);

        diagnoses.Answer("alpha", "session-state", "machines", "one");
        diagnoses.Answer("beta", "session-state", "machines", "several");

        diagnoses.ForgetAll("alpha");

        Assert.Empty(diagnoses.Answers("alpha", "session-state"));
        Assert.Single(diagnoses.Answers("beta", "session-state"));
    }

    [Fact]
    public void What_is_under_way_is_counted_per_dilemma()
    {
        using var store = Store();
        var diagnoses = new Diagnoses(store.Connection);

        diagnoses.Answer("alpha", "session-state", "machines", "one");
        diagnoses.Answer("alpha", "session-state", "size", "small");
        diagnoses.Answer("alpha", "output-cache", "shared", "yes");

        var started = diagnoses.Started("alpha");

        Assert.Equal(2, started["session-state"]);
        Assert.Equal(1, started["output-cache"]);
    }

    [Fact]
    public void An_answer_to_a_question_the_catalogue_no_longer_has_is_skipped_rather_than_fatal()
    {
        // The catalogue is edited between releases. A diagnosis recorded last
        // month has to still read correctly today, and a stored answer that no
        // longer matches anything must not take the panel down with it.
        using var store = Store();
        var diagnoses = new Diagnoses(store.Connection);

        diagnoses.Answer("alpha", "session-state", "retired-question", "whatever");
        diagnoses.Answer("alpha", "session-state", "machines", "several");

        var diagnosis = new Diagnosis(Sticky(), diagnoses.Answers("alpha", "session-state"));

        Assert.Equal(["distributed"], diagnosis.Remaining.Select(o => o.Id));
        Assert.Single(diagnosis.Reasoning);
    }
}
