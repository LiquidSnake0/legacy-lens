using LegacyLens.Api.Generation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LegacyLens.Tests;

/// <summary>
/// Which model answers.
///
/// The default matters more than the choice: a tool whose front page says no
/// source code leaves the machine has to keep that true unless someone
/// deliberately asks otherwise, and asking otherwise has to be impossible by
/// accident.
/// </summary>
public class ChatClientsTests
{
    private static ChatClients Clients(Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddHttpClient();

        return new ChatClients(
            new LocalStub(),
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            config);
    }

    [Fact]
    public void No_choice_means_the_local_model()
    {
        Assert.IsType<LocalStub>(Clients().For(null));
    }

    [Fact]
    public void Asking_for_local_means_the_local_model()
    {
        Assert.IsType<LocalStub>(Clients().For(new ModelChoice(ModelChoice.Local)));
    }

    [Fact]
    public void An_unrecognised_provider_falls_back_to_local_rather_than_failing()
    {
        // The safe direction. A typo that silently sent the code out would be
        // the one failure mode this class exists to prevent.
        Assert.IsType<LocalStub>(Clients().For(new ModelChoice("openai-ish")));
    }

    [Fact]
    public void A_hosted_model_needs_the_reader_to_bring_a_key()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => Clients().For(new ModelChoice(ModelChoice.Hosted)));

        Assert.Contains("your own API key", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never stored", failure.Message);
    }

    [Fact]
    public void An_empty_key_is_the_same_as_no_key()
    {
        Assert.Throws<ArgumentException>(
            () => Clients().For(new ModelChoice(ModelChoice.Hosted, ApiKey: "   ")));
    }

    [Fact]
    public void A_hosted_model_with_a_key_is_a_hosted_client()
    {
        var client = Clients().For(new ModelChoice(ModelChoice.Hosted, "gpt-4o", "sk-test"));

        Assert.IsType<HostedChatClient>(client);
    }

    [Fact]
    public void Where_a_hosted_request_goes_is_configuration_and_not_the_caller_s_to_pick()
    {
        // ModelChoice carries a provider, a model and a key, and deliberately
        // no URL: a page that could name the host could be told to name one by
        // anything that reaches it.
        Assert.DoesNotContain(
            typeof(ModelChoice).GetProperties(),
            property => property.Name.Contains("Url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_options_name_the_local_model_and_say_what_stays_here()
    {
        var options = Clients(new Dictionary<string, string?>
        {
            ["HOSTED_URL"] = "https://api.example/v1",
            ["HOSTED_MODEL"] = "some-model",
        }).Options;

        Assert.Equal("https://api.example/v1", options.HostedUrl);
        Assert.Equal("some-model", options.DefaultHostedModel);
    }

    private class LocalStub : IChatClient
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult("local");

        public async IAsyncEnumerable<string> StreamAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return "local";
        }
    }
}
