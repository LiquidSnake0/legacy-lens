using LegacyLens.Analysis;
using Microsoft.Data.Sqlite;

namespace LegacyLens.Api.Storage;

/// <summary>
/// What somebody said about a dilemma, kept.
///
/// Only the answers are stored. Everything else about a diagnosis, what is
/// still possible, what to ask next, whether it has landed, is a fold over
/// them, so there is no second copy of the state to drift out of step with the
/// first. Recomputing four booleans is cheaper than reconciling them.
///
/// It also survives the catalogue changing underneath it. An answer to a
/// question that no longer exists is skipped by the fold rather than
/// crashing it, and a diagnosis recorded last month still reads correctly when
/// a new outcome is added to the dilemma today.
/// </summary>
public class Diagnoses
{
    private readonly SqliteConnection _connection;

    public Diagnoses(SqliteConnection connection)
    {
        _connection = connection;
        Migrate();
    }

    private void Migrate() => Execute("""
        CREATE TABLE IF NOT EXISTS diagnosis_answers (
            workspace_id TEXT NOT NULL,
            dilemma_id   TEXT NOT NULL,
            question_id  TEXT NOT NULL,
            answer       TEXT NOT NULL,
            answered_at  TEXT NOT NULL,
            PRIMARY KEY (workspace_id, dilemma_id, question_id)
        );
        """);

    /// <summary>
    /// Records an answer, replacing any earlier one to the same question.
    ///
    /// People change their minds halfway through, and a diagnosis holding both
    /// answers is one that contradicts itself. The replacement keeps its
    /// original place in the order, so the reasoning still reads in the
    /// sequence it was actually asked.
    /// </summary>
    public void Answer(string workspace, string dilemmaId, string questionId, string answer) =>
        Execute("""
            INSERT INTO diagnosis_answers
                (workspace_id, dilemma_id, question_id, answer, answered_at)
            VALUES ($workspace, $dilemma, $question, $answer, $now)
            ON CONFLICT (workspace_id, dilemma_id, question_id)
            DO UPDATE SET answer = excluded.answer, answered_at = excluded.answered_at;
            """,
            ("$workspace", workspace),
            ("$dilemma", dilemmaId),
            ("$question", questionId),
            ("$answer", answer),
            ("$now", DateTimeOffset.UtcNow.ToString("o")));

    public IReadOnlyList<Answered> Answers(string workspace, string dilemmaId)
    {
        using var command = _connection.CreateCommand();

        // By rowid, which is the order they were first given in. An upsert
        // keeps the row, so changing an answer does not move it to the end and
        // reorder somebody's own reasoning under them.
        command.CommandText = """
            SELECT question_id, answer FROM diagnosis_answers
            WHERE workspace_id = $workspace AND dilemma_id = $dilemma
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$workspace", workspace);
        command.Parameters.AddWithValue("$dilemma", dilemmaId);

        using var reader = command.ExecuteReader();

        var answers = new List<Answered>();
        while (reader.Read()) answers.Add(new Answered(reader.GetString(0), reader.GetString(1)));

        return answers;
    }

    /// <summary>How many answers stand per dilemma, for showing what is under way.</summary>
    public IReadOnlyDictionary<string, int> Started(string workspace)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT dilemma_id, COUNT(*) FROM diagnosis_answers
            WHERE workspace_id = $workspace GROUP BY dilemma_id;
            """;
        command.Parameters.AddWithValue("$workspace", workspace);

        using var reader = command.ExecuteReader();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt32(1);

        return counts;
    }

    /// <summary>Starts one over. Nothing else is touched.</summary>
    public int Forget(string workspace, string dilemmaId) => Execute("""
        DELETE FROM diagnosis_answers
        WHERE workspace_id = $workspace AND dilemma_id = $dilemma;
        """,
        ("$workspace", workspace),
        ("$dilemma", dilemmaId));

    /// <summary>
    /// Drops everything a workspace answered.
    ///
    /// Called when the workspace goes. Answers about code that is no longer
    /// indexed would sit there being wrong, and would be handed to whichever
    /// workspace later reuses the identifier.
    /// </summary>
    public int ForgetAll(string workspace) => Execute(
        "DELETE FROM diagnosis_answers WHERE workspace_id = $workspace;",
        ("$workspace", workspace));

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }
}
