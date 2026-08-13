using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LegacyLens.Api.Generation;

public interface IChatClient
{
    Task<string> CompleteAsync(string prompt, CancellationToken ct = default);
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

    private record OllamaGenerateRequest(
        [property: JsonPropertyName("model")]  string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] OllamaOptions Options);

    private record OllamaOptions(
        [property: JsonPropertyName("temperature")] float Temperature,
        [property: JsonPropertyName("num_ctx")]     int NumCtx);

    private record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string? Response);
}
