using System.Text.Json;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LegacyLens.Api.Generation;

public interface IChatClient
{
    Task<string> CompleteAsync(string prompt, CancellationToken ct = default);

    /// <summary>
    /// The same completion, yielded as it is produced.
    ///
    /// On a CPU a 3B model takes tens of seconds to finish an answer. Watching
    /// a blank screen for that long reads as a crash, and users reload the page
    /// well before the answer arrives.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default);
}

/// <summary>Generation against a local Ollama instance. Nothing leaves the machine.</summary>
public class OllamaChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;

    public OllamaChatClient(HttpClient http, IConfiguration config)
    {
        _http = http;
        _model = config["CHAT_MODEL"] ?? "qwen2.5-coder:3b";
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/generate", new OllamaGenerateRequest(
            Model: _model,
            Prompt: prompt,
            Stream: false,
            // Low temperature: this is a question-answering tool over supplied
            // evidence, not a writing assistant. Creativity here is called
            // hallucination.
            Options: new OllamaOptions(Temperature: 0.1f, NumCtx: 8192)), ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Ollama returned {(int)response.StatusCode}. " +
                $"Is the model pulled? `ollama pull {_model}`. Body: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
        return payload?.Response ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = JsonContent.Create(new OllamaGenerateRequest(
                Model: _model,
                Prompt: prompt,
                Stream: true,
                Options: new OllamaOptions(Temperature: 0.1f, NumCtx: 8192))),
        };

        // Without this the handler buffers the whole body before returning, and
        // the stream arrives all at once at the end, which is the opposite of
        // the point.
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Ollama returned {(int)response.StatusCode}. " +
                $"Is the model pulled? `ollama pull {_model}`. Body: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Ollama streams newline-delimited JSON, one object per token.
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;

            OllamaGenerateResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line);
            }
            catch (JsonException)
            {
                // A truncated line is not worth aborting a running answer over.
                continue;
            }

            if (!string.IsNullOrEmpty(chunk?.Response)) yield return chunk.Response;
            if (chunk?.Done == true) yield break;
        }
    }

    private record OllamaGenerateRequest(
        [property: JsonPropertyName("model")]  string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] OllamaOptions Options);

    private record OllamaOptions(
        [property: JsonPropertyName("temperature")] float Temperature,
        [property: JsonPropertyName("num_ctx")]     int NumCtx);

    private record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("done")] bool Done = false);
}
