using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LegacyLens.Api.Ingestion;

/// <summary>Where a clone ended up, or why there is no clone.</summary>
public record CloneResult(bool Ok, string? Path, string? Error);

/// <summary>
/// Fetching a repository so it can be indexed.
///
/// The alternative was to make people clone it themselves and paste a path,
/// which is fine for the person who wrote the tool and a wall for everyone
/// else.
/// </summary>
public partial class Cloning
{
    private readonly ILogger<Cloning> _log;

    public Cloning(ILogger<Cloning> log) => _log = log;

    /// <summary>
    /// Only what git can be handed safely.
    ///
    /// git understands transports beyond http: `ext::` runs an arbitrary
    /// command, and `file://` reads the host's disk. Neither is something a
    /// URL typed into a web form should be able to reach, so the check is a
    /// list of what is allowed rather than a list of what is not.
    /// </summary>
    public static bool IsAcceptable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrEmpty(parsed.Host);

    /// <summary>
    /// A directory name that is recognisably the repository and cannot escape
    /// the folder it belongs in.
    /// </summary>
    public static string FolderFor(string url, string workspaceId)
    {
        var last = Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.Segments.LastOrDefault()?.Trim('/') ?? string.Empty
            : string.Empty;

        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) last = last[..^4];

        var safe = Unsafe().Replace(last, "-").Trim('-');

        // The workspace id is appended rather than trusted alone: two
        // workspaces on the same repository must not land in one directory,
        // and a repository called ".." must not land anywhere interesting.
        return safe.Length == 0 ? workspaceId : $"{safe}-{workspaceId}";
    }

    /// <summary>
    /// Clones into <paramref name="into"/>, with the token used and forgotten.
    ///
    /// Full history, not a shallow clone: the risk ranking reads change
    /// frequency from git, and a shallow clone would leave it ranking on
    /// structure alone without saying so.
    /// </summary>
    public async Task<CloneResult> CloneAsync(
        string url, string into, string? token, CancellationToken ct = default)
    {
        if (!IsAcceptable(url))
            return new CloneResult(false, null, "Only http and https repository URLs are accepted.");

        if (Directory.Exists(into) && Directory.EnumerateFileSystemEntries(into).Any())
            return new CloneResult(false, null, $"{into} already exists and is not empty.");

        Directory.CreateDirectory(into);

        var authenticated = WithToken(url, token);

        var arguments = new List<string> { "clone", authenticated, into };

        var (ok, error) = await RunAsync(arguments, workingDirectory: null, ct);

        if (!ok)
        {
            TryRemove(into);

            // The token would otherwise be echoed back to the browser inside
            // git's own error message.
            return new CloneResult(false, null, Redact(error, token));
        }

        // git wrote the URL it was given into .git/config, token and all. The
        // remote is reset to the clean URL so the credential is not left on
        // disk for anyone who opens the file later.
        if (!string.IsNullOrEmpty(token))
        {
            var (reset, resetError) = await RunAsync(
                ["remote", "set-url", "origin", url], into, ct);

            if (!reset)
            {
                TryRemove(into);
                return new CloneResult(false, null,
                    "Cloned, but the access token could not be removed from the clone, " +
                    $"so it was deleted rather than left on disk. {Redact(resetError, token)}");
            }
        }

        _log.LogInformation("Cloned {Url} into {Path}", url, into);
        return new CloneResult(true, into, null);
    }

    /// <summary>
    /// Puts the token where git expects it for an HTTPS clone.
    ///
    /// Held only for the length of this call. Nothing writes it to the index,
    /// the workspace row or the log.
    /// </summary>
    private static string WithToken(string url, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return url;

        var parsed = new UriBuilder(url)
        {
            // The username is ignored by GitHub, GitLab and Bitbucket alike
            // when a token is supplied, but one has to be there.
            UserName = "git",
            Password = token,
        };

        return parsed.Uri.ToString();
    }

    private static string Redact(string text, string? token) =>
        string.IsNullOrEmpty(token) ? text : text.Replace(token, "***");

    private static void TryRemove(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // A half-written clone left behind is untidy. Throwing here would
            // replace a useful error message with a useless one.
        }
    }

    private async Task<(bool Ok, string Error)> RunAsync(
        IEnumerable<string> arguments, string? workingDirectory, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // No shell, and the arguments are passed as a list, so a URL
            // containing a space or a quote is one argument rather than three.
            UseShellExecute = false,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;

        // Git will otherwise open a terminal prompt for credentials on a
        // private repository and hang there until the request times out.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ASKPASS"] = string.Empty;

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("git did not start.");

            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode == 0, error);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return (false, $"Could not run git: {failure.Message}");
        }
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex Unsafe();
}
