using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegacyLens.Analysis;

/// <summary>One of the finitely many places this can end.</summary>
public record Outcome(string Id, string Name, string Note);

/// <summary>An answer, and what it rules out.</summary>
public record Choice(string Answer, IReadOnlyList<string> Eliminates, string Because);

/// <summary>Something the code cannot say, asked once.</summary>
public record Question(string Id, string Ask, string Why, IReadOnlyList<Choice> Choices);

/// <summary>
/// A decision the code cannot make on its own.
///
/// Triggered by names in the source, and answered by a person. The outcomes are
/// written down first and the questions exist only to eliminate them, which is
/// what separates this from a conversation: there is a known place to land, and
/// a question that rules nothing out is never asked.
/// </summary>
public record Dilemma(
    string Id,
    string Name,
    /// <summary>Type names whose presence raises it.</summary>
    IReadOnlyList<string> Triggers,
    string What,
    IReadOnlyList<Outcome> Outcomes,
    IReadOnlyList<Question> Questions);

public record DilemmaCatalogue(IReadOnlyList<Dilemma> Dilemmas, string Source)
{
    public Dilemma? Find(string id) =>
        Dilemmas.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Which dilemmas a set of type names raises.</summary>
    public IReadOnlyList<Dilemma> RaisedBy(IEnumerable<string> names)
    {
        var seen = names.ToHashSet(StringComparer.Ordinal);

        return Dilemmas
            .Where(d => d.Triggers.Any(seen.Contains))
            .ToList();
    }
}

/// <summary>One answer somebody gave.</summary>
public record Answered(string QuestionId, string Answer);

/// <summary>
/// A diagnosis in progress: what is still possible, and what to ask next.
///
/// The whole thing is a fold over answers. Nothing is stored but the answers,
/// so the state is reconstructible, auditable, and impossible to get subtly out
/// of step with itself.
/// </summary>
public record Diagnosis(Dilemma Dilemma, IReadOnlyList<Answered> Answers)
{
    /// <summary>The outcomes nothing has ruled out yet.</summary>
    public IReadOnlyList<Outcome> Remaining
    {
        get
        {
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var answered in Answers)
            {
                var question = Dilemma.Questions
                    .FirstOrDefault(q => q.Id.Equals(answered.QuestionId, StringComparison.OrdinalIgnoreCase));

                var choice = question?.Choices
                    .FirstOrDefault(c => c.Answer.Equals(answered.Answer, StringComparison.OrdinalIgnoreCase));

                if (choice is null) continue;

                foreach (var id in choice.Eliminates) gone.Add(id);
            }

            return Dilemma.Outcomes.Where(o => !gone.Contains(o.Id)).ToList();
        }
    }

    /// <summary>
    /// The next question worth asking, or none.
    ///
    /// Worth asking means at least one of its answers would rule out something
    /// still standing. That is the stopping condition, and it is a measurement
    /// rather than a limit: a model with no such rule asks until the reader
    /// closes the tab.
    /// </summary>
    public Question? Next
    {
        get
        {
            var asked = Answers.Select(a => a.QuestionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var standing = Remaining.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // One outcome left is an answer. Asking more would be theatre.
            if (standing.Count <= 1) return null;

            return Dilemma.Questions.FirstOrDefault(q =>
                !asked.Contains(q.Id)
                && q.Choices.Any(c => c.Eliminates.Any(standing.Contains)));
        }
    }

    public bool Settled => Next is null;

    /// <summary>
    /// What the answers add up to, in the words a person would use.
    ///
    /// Every line traces to a measured fact or to a sentence somebody owned, so
    /// that six months later the trail is still there when the decision is
    /// questioned.
    /// </summary>
    public IReadOnlyList<string> Reasoning
    {
        get
        {
            var said = new List<string>();

            foreach (var answered in Answers)
            {
                var question = Dilemma.Questions
                    .FirstOrDefault(q => q.Id.Equals(answered.QuestionId, StringComparison.OrdinalIgnoreCase));

                var choice = question?.Choices
                    .FirstOrDefault(c => c.Answer.Equals(answered.Answer, StringComparison.OrdinalIgnoreCase));

                if (question is null || choice is null) continue;

                said.Add($"You said: {question.Ask} {choice.Answer}. {choice.Because}");
            }

            return said;
        }
    }
}

/// <summary>
/// Reads the dilemmas, which are data rather than code.
///
/// The questions are written down rather than produced on demand, and that is
/// the design rather than a shortcut. A model asked what to ask produces
/// plausible questions with no known set of answers behind them, and a
/// diagnosis that cannot say where it will land is a conversation. These land
/// in a place chosen before anybody was asked anything.
///
/// What no catalogue can do is know how many machines are behind the load
/// balancer. That is why a person is in this loop, and the only reason.
/// </summary>
public class Dilemmas
{
    private static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static DilemmaCatalogue Load(string? path = null)
    {
        var file = path ?? Default();

        if (file is null || !File.Exists(file))
        {
            return new DilemmaCatalogue([],
                file is null ? "no dilemmas were found" : $"no dilemmas at {file}");
        }

        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(file), Reading) ?? [];

            var dilemmas = new List<Dilemma>();

            foreach (var (id, value) in read)
            {
                // Keys beginning with // carry the reasoning. This file is
                // written by hand and a hand-written file is commented.
                if (id.StartsWith("//", StringComparison.Ordinal)) continue;
                if (value.ValueKind != JsonValueKind.Object) continue;

                var entry = value.Deserialize<Entry>(Reading);
                if (entry is null) continue;

                dilemmas.Add(new Dilemma(
                    id,
                    entry.Name ?? id,
                    entry.Triggers ?? [],
                    entry.What ?? string.Empty,
                    (entry.Outcomes ?? []).Select(o =>
                        new Outcome(o.Id, o.Name ?? o.Id, o.Note ?? string.Empty)).ToList(),
                    (entry.Questions ?? []).Select(q => new Question(
                        q.Id,
                        q.Ask ?? string.Empty,
                        q.Why ?? string.Empty,
                        (q.Choices ?? []).Select(c => new Choice(
                            c.Answer,
                            c.Eliminates ?? [],
                            c.Because ?? string.Empty)).ToList())).ToList()));
            }

            return new DilemmaCatalogue(dilemmas, file);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new DilemmaCatalogue([], $"{file} could not be read: {exception.Message}");
        }
    }

    private static string? Default()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "dilemmas.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "dilemmas.json"),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var up = 0; up < 6 && directory is not null; up++)
        {
            candidates.Add(Path.Combine(directory.FullName, "data", "dilemmas.json"));
            directory = directory.Parent;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record Entry(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("triggers")] List<string>? Triggers,
        [property: JsonPropertyName("what")] string? What,
        [property: JsonPropertyName("outcomes")] List<OutcomeEntry>? Outcomes,
        [property: JsonPropertyName("questions")] List<QuestionEntry>? Questions);

    private sealed record OutcomeEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("note")] string? Note);

    private sealed record QuestionEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("ask")] string? Ask,
        [property: JsonPropertyName("why")] string? Why,
        [property: JsonPropertyName("choices")] List<ChoiceEntry>? Choices);

    private sealed record ChoiceEntry(
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("eliminates")] List<string>? Eliminates,
        [property: JsonPropertyName("because")] string? Because);
}
