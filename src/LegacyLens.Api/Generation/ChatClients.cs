namespace LegacyLens.Api.Generation;

/// <summary>
/// Which model answers, and with whose key.
///
/// The key travels with the request and is used for that request. Nothing
/// writes it down: not the index, not a settings file, not the log. That is
/// the whole reason it is asked for per call rather than configured once.
/// </summary>
public record ModelChoice(string Provider, string? Model = null, string? ApiKey = null)
{
    public const string Local = "local";
    public const string Hosted = "hosted";
}

/// <summary>What the interface should offer, and what it should warn about.</summary>
public record ModelOptions(
    string LocalModel,
    bool HostedAvailable,
    string HostedUrl,
    string DefaultHostedModel);

public interface IChatClients
{
    /// <summary>The client for a choice, or the local one when there is none.</summary>
    IChatClient For(ModelChoice? choice);

    ModelOptions Options { get; }
}

/// <summary>
/// Local by default, hosted if the reader brings a key.
///
/// Both keep the operator out of the loop, which is the point: hosting a
/// shared instance with a shared key is a different product with different
/// obligations, and deliberately not this one.
///
/// Only generation is switchable. Embeddings stay local whatever is chosen,
/// because embedding is the half that reads *every* file: sending it out would
/// upload the entire codebase, where generation sends only the handful of
/// excerpts retrieved for one question. That difference is the reason the
/// choice is offered at all.
/// </summary>
public class ChatClients : IChatClients
{
    private readonly IChatClient _local;
    private readonly IHttpClientFactory _http;
    private readonly string _hostedUrl;
    private readonly string _defaultHostedModel;

    public ChatClients(IChatClient local, IHttpClientFactory http, IConfiguration config)
    {
        _local = local;
        _http = http;

        // The host comes from configuration, not from the request. The key is
        // the reader's to supply; where their code excerpts get posted is the
        // operator's decision, and a browser that could name any host could be
        // pointed at one by anything that reaches the page.
        _hostedUrl = config["HOSTED_URL"] ?? "https://api.openai.com/v1";
        _defaultHostedModel = config["HOSTED_MODEL"] ?? "gpt-4o-mini";
    }

    public ModelOptions Options => new(
        LocalModel: _local is OllamaChatClient ollama ? ollama.Model : "local",
        HostedAvailable: true,
        HostedUrl: _hostedUrl,
        DefaultHostedModel: _defaultHostedModel);

    public IChatClient For(ModelChoice? choice)
    {
        if (choice is null || choice.Provider != ModelChoice.Hosted) return _local;

        if (string.IsNullOrWhiteSpace(choice.ApiKey))
        {
            throw new ArgumentException(
                "A hosted model needs your own API key. It is used for this " +
                "request and never stored.");
        }

        return new HostedChatClient(
            _http.CreateClient("hosted"),
            _hostedUrl,
            choice.Model ?? _defaultHostedModel,
            choice.ApiKey);
    }
}
