using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegacyLens.Api.Generation;

/// <summary>
/// Generation against an OpenAI-compatible endpoint, with the reader's key.
///
/// One shape rather than one per vendor: chat completions is what OpenAI,
/// Groq, Together, OpenRouter and most local servers all speak, so pointing
/// HOSTED_URL somewhere else is configuration rather than another class.
///
/// What leaves the machine when this is used: the question, and the excerpts
/// retrieved for it. Not the codebase. The interface says so before the choice
/// is made, because a tool whose front page reads "no source code leaves it"
/// owes the reader that sentence at the moment it stops being true.
/// </summary>
public class HostedChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;

    public HostedChatClient(HttpClient http, string baseUrl, string model, string apiKey)
    {
        _http = http;
        _model = model;

        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "chat/completions", Request(prompt, stream: false), ct);

        await Refuse(response, ct);

        var payload = await response.Content.ReadFromJsonAsync<CompletionResponse>(ct);
        return payload?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(Request(prompt, stream: true)),
        };

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        await Refuse(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Server-sent events: "data: {json}" lines, ended by "data: [DONE]".
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") yield break;

            CompletionChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<CompletionChunk>(payload);
            }
            catch (JsonException)
            {
                // A truncated line is not worth aborting a running answer over.
                continue;
            }

            var text = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    private object Request(string prompt, bool stream) => new
    {
        model = _model,
        // Same as the local client: this answers from supplied evidence, and
        // creativity here is the other word for hallucination.
        temperature = 0.1f,
        stream,
        messages = new[] { new { role = "user", content = prompt } },
    };

    /// <summary>
    /// Turns a refusal into something the reader can act on.
    ///
    /// 401 from a hosted provider means the key, and saying so beats a stack
    /// trace that sends them looking at the index.
    /// </summary>
    private static async Task Refuse(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);

        throw new InvalidOperationException((int)response.StatusCode switch
        {
            401 or 403 => "The hosted provider rejected the API key.",
            429 => "The hosted provider is rate limiting this key.",
            _ => $"The hosted provider returned {(int)response.StatusCode}. {body}",
        });
    }

    private record CompletionResponse(
        [property: JsonPropertyName("choices")] List<Choice>? Choices);

    private record Choice(
        [property: JsonPropertyName("message")] Message? Message);

    private record Message(
        [property: JsonPropertyName("content")] string? Content);

    private record CompletionChunk(
        [property: JsonPropertyName("choices")] List<ChunkChoice>? Choices);

    private record ChunkChoice(
        [property: JsonPropertyName("delta")] Delta? Delta);

    private record Delta(
        [property: JsonPropertyName("content")] string? Content);
}
