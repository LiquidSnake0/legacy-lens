using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LegacyLens.Api.Embeddings;

public interface IEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>
/// Embeddings from a local Ollama instance. This half never leaves the machine,
/// whatever the generation provider is set to.
/// </summary>
public class OllamaEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<OllamaEmbeddingClient> _log;

    public OllamaEmbeddingClient(HttpClient http, IConfiguration config, ILogger<OllamaEmbeddingClient> log)
    {
        _http = http;
        _model = config["EMBED_MODEL"] ?? "nomic-embed-text";
        _log = log;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "/api/embeddings", new OllamaEmbedRequest(_model, text), ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Ollama returned {(int)response.StatusCode} for embeddings. " +
                $"Is the model pulled? `ollama pull {_model}`. Body: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct);
        if (payload?.Embedding is null or { Length: 0 })
            throw new InvalidOperationException("Ollama returned an empty embedding.");

        return payload.Embedding;
    }

    /// <summary>
    /// Sequential on purpose: Ollama serialises requests internally, so firing
    /// them in parallel buys nothing and makes progress logging useless.
    /// </summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(await EmbedAsync(texts[i], ct));

            if ((i + 1) % 100 == 0)
                _log.LogInformation("Embedded {Done}/{Total} chunks", i + 1, texts.Count);
        }
        return result;
    }

    private record OllamaEmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt);

    private record OllamaEmbedResponse(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
