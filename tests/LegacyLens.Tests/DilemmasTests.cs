using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The questioner.
///
/// What separates this from a conversation is that the outcomes are written
/// down before anybody is asked anything, so there is always a known place to
/// land, and a question that rules nothing out is never asked. A model with no
/// such rule asks until the reader closes the tab.
/// </summary>
public class DilemmasTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-dilemmas-{Guid.NewGuid():N}");

    public DilemmasTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Catalogue(string json)
    {
        var path = Path.Combine(_root, "dilemmas.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Two outcomes, one question that tells them apart.</summary>
    private const string Simple = """
        {
          "//": "commentary the loader must survive",
          "pick": {
            "name": "A choice",
            "triggers": ["Trigger"],
            "what": "the code cannot say",
            "outcomes": [
              { "id": "a", "name": "First", "note": "one" },
              { "id": "b", "name": "Second", "note": "two" }
            ],
            "questions": [
              {
                "id": "q1",
                "ask": "Is it so?",
                "why": "because",
                "choices": [
                  { "answer": "yes", "eliminates": ["a"], "because": "so not the first" },
                  { "answer": "no", "eliminates": ["b"], "because": "so not the second" }
                ]
              }
            ]
          }
        }
        """;

    private Dilemma Pick(string json = Simple) =>
        Dilemmas.Load(Catalogue(json)).Find("pick")!;

    /* ---- reading them ---- */

    [Fact]
    public void A_catalogue_is_read_from_a_file_with_its_commentary_intact()
    {
        var catalogue = Dilemmas.Load(Catalogue(Simple));

        var dilemma = Assert.Single(catalogue.Dilemmas);
        Assert.Equal("pick", dilemma.Id);
        Assert.Equal(2, dilemma.Outcomes.Count);
        Assert.Single(dilemma.Questions);
    }

    [Fact]
    public void A_missing_catalogue_says_where_it_looked_rather_than_throwing()
    {
        var catalogue = Dilemmas.Load(Path.Combine(_root, "nowhere.json"));

        Assert.Empty(catalogue.Dilemmas);
        Assert.Contains("nowhere.json", catalogue.Source);
    }

    [Fact]
    public void Only_the_dilemmas_the_code_raises_are_offered()
    {
        var catalogue = Dilemmas.Load(Catalogue(Simple));

        Assert.Single(catalogue.RaisedBy(["Trigger", "Unrelated"]));
        Assert.Empty(catalogue.RaisedBy(["Unrelated"]));
    }

    /* ---- the fold ---- */

    [Fact]
    public void Nothing_is_ruled_out_before_anybody_answers()
    {
        var diagnosis = new Diagnosis(Pick(), []);

        Assert.Equal(2, diagnosis.Remaining.Count);
        Assert.False(diagnosis.Settled);
        Assert.Equal("q1", diagnosis.Next?.Id);
    }

    [Fact]
    public void An_answer_rules_out_what_it_says_it_rules_out()
    {
        var diagnosis = new Diagnosis(Pick(), [new Answered("q1", "yes")]);

        Assert.Equal(["b"], diagnosis.Remaining.Select(o => o.Id));
    }

    [Fact]
    public void One_outcome_left_is_an_answer_rather_than_a_reason_to_keep_asking()
    {
        // Asking more would be theatre: the destination is already known.
        var diagnosis = new Diagnosis(Pick(), [new Answered("q1", "yes")]);

        Assert.True(diagnosis.Settled);
        Assert.Null(diagnosis.Next);
    }

    [Fact]
    public void An_answer_nobody_offered_changes_nothing()
    {
        // A typo or a stale form must not silently eliminate an outcome.
        var diagnosis = new Diagnosis(Pick(), [new Answered("q1", "maybe")]);

        Assert.Equal(2, diagnosis.Remaining.Count);
    }

    [Fact]
    public void A_question_that_would_rule_nothing_out_is_not_asked()
    {
        // The stopping condition, and it is a measurement rather than a limit.
        var json = """
            {
              "pick": {
                "outcomes": [
                  { "id": "a" }, { "id": "b" }, { "id": "c" }
                ],
                "questions": [
                  {
                    "id": "q1",
                    "ask": "first",
                    "choices": [ { "answer": "yes", "eliminates": ["a"] } ]
                  },
                  {
                    "id": "q2",
                    "ask": "about something already gone",
                    "choices": [ { "answer": "yes", "eliminates": ["a"] } ]
                  },
                  {
                    "id": "q3",
                    "ask": "still useful",
                    "choices": [ { "answer": "yes", "eliminates": ["b"] } ]
                  }
                ]
              }
            }
            """;

        var diagnosis = new Diagnosis(Dilemmas.Load(Catalogue(json)).Find("pick")!,
            [new Answered("q1", "yes")]);

        // q2 only eliminates something already eliminated, so it is skipped.
        Assert.Equal("q3", diagnosis.Next?.Id);
    }

    [Fact]
    public void The_reasoning_says_what_the_person_said_and_why_it_mattered()
    {
        // Six months later, when the decision is questioned, the trail is
        // still there and it names who said what.
        var diagnosis = new Diagnosis(Pick(), [new Answered("q1", "yes")]);

        var line = Assert.Single(diagnosis.Reasoning);
        Assert.StartsWith("You said:", line);
        Assert.Contains("Is it so?", line);
        Assert.Contains("so not the first", line);
    }

    [Fact]
    public void The_state_is_the_answers_and_nothing_else()
    {
        // Rebuilt from the answers every time, so it cannot drift out of step
        // with itself, and a session is auditable by reading a list.
        var dilemma = Pick();

        var first = new Diagnosis(dilemma, [new Answered("q1", "no")]);
        var rebuilt = new Diagnosis(dilemma, first.Answers);

        Assert.Equal(
            first.Remaining.Select(o => o.Id),
            rebuilt.Remaining.Select(o => o.Id));
    }

    /* ---- the shipped catalogue ---- */

    [Fact]
    public void The_dilemmas_in_the_repository_load_and_land_somewhere()
    {
        var catalogue = Dilemmas.Load();
        if (catalogue.Dilemmas.Count == 0) return;

        var session = catalogue.Find("session-state");
        Assert.NotNull(session);
        Assert.Contains("HttpSessionState", session.Triggers);

        // Answering every question has to leave at most one outcome, or the
        // catalogue asks questions that lead nowhere.
        var answers = new List<Answered>();
        var diagnosis = new Diagnosis(session, answers);

        while (diagnosis.Next is { } question)
        {
            answers.Add(new Answered(question.Id, question.Choices[0].Answer));
            diagnosis = new Diagnosis(session, answers);
        }

        Assert.True(diagnosis.Remaining.Count <= 1,
            $"left {diagnosis.Remaining.Count} outcomes standing");
    }

    [Fact]
    public void Every_shipped_question_can_rule_something_out()
    {
        // A question that eliminates an outcome nobody declared is a typo, and
        // it would be asked forever without ever narrowing anything.
        var catalogue = Dilemmas.Load();
        if (catalogue.Dilemmas.Count == 0) return;

        foreach (var dilemma in catalogue.Dilemmas)
        {
            var outcomes = dilemma.Outcomes.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var question in dilemma.Questions)
            {
                foreach (var choice in question.Choices)
                {
                    foreach (var eliminated in choice.Eliminates)
                    {
                        Assert.True(outcomes.Contains(eliminated),
                            $"{dilemma.Id}/{question.Id}/{choice.Answer} rules out "
                            + $"\"{eliminated}\", which {dilemma.Id} does not declare");
                    }
                }
            }
        }
    }
}
