using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegacyLens.Characterization;

/// <summary>
/// An equivalence report, written down so it can cross a process boundary.
///
/// Only the facts travel. What was compiled, what was compared, what moved,
/// what was passed over: those are things that happened, and the process that
/// watched them happen is the only one that can report them.
///
/// The sentences do not travel. <see cref="EquivalenceReport.Claim"/>,
/// <see cref="EquivalenceReport.Verified"/> and the grouped refusals are
/// readings of those facts, and they are recomputed on this side from the facts
/// that arrived. That is the point of the split: a claim transmitted as text
/// could disagree with the numbers printed beside it, and the one sentence in
/// this whole tool that must never be wrong is the one that says nothing moved.
/// The derived members carry <see cref="JsonIgnoreAttribute"/> to say so in the
/// place a reader will look.
/// </summary>
public static class Wire
{
    /// <summary>
    /// Shared by both sides, because a format written with one set of options
    /// and read with another is a bug that only appears once the two halves are
    /// built separately.
    ///
    /// Reasons travel as their names rather than their numbers. Inserting a
    /// value into the middle of <see cref="SkipReason"/> would otherwise
    /// silently relabel every refusal a slightly older child reported.
    /// </summary>
    private static readonly JsonSerializerOptions Format = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(EquivalenceReport report) =>
        JsonSerializer.Serialize(report, Format);

    /// <summary>
    /// Null when what arrived is not a report.
    ///
    /// A caller has to decide what to say about that, and it is never "nothing
    /// moved". Returning null rather than throwing keeps that decision at the
    /// place that knows what it was expecting.
    /// </summary>
    public static EquivalenceReport? Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<EquivalenceReport>(json, Format);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
