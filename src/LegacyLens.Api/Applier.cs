using System.Diagnostics;
using System.Text;

namespace LegacyLens.Api;

/// <summary>Where a patch landed, or why it did not.</summary>
public record Landed(
    bool Applied,
    string? Branch,
    string? Commit,
    int Files,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<string> Notes);

/// <summary>
/// Puts a patch on a branch of its own, and leaves you where you were.
///
/// The rule this milestone rests on is that the tool proposes and a person
/// approves. A button does not break that: clicking after reading is a person
/// approving. What would break it is writing into the working tree, where the
/// change has no history, no second reader and no way back.
///
/// So it makes a branch, commits there, and checks the original branch out
/// again. Nothing moves under an open editor, and the result is a branch to
/// diff, merge or delete.
///
/// It does not push and it does not open a pull request. That needs a remote
/// and a credential, and pushing someone's code somewhere is a decision that
/// belongs to them.
/// </summary>
public class Applier
{
    private readonly ILogger<Applier> _log;

    public Applier(ILogger<Applier> log) => _log = log;

    public Landed Apply(string rootPath, string kind, string patch)
    {
        var refusals = new List<string>();

        if (string.IsNullOrWhiteSpace(patch))
            return Refused("There is no patch to apply.");

        if (!Directory.Exists(rootPath))
            return Refused($"No such directory: {rootPath}.");

        // Without git there is no branch to isolate this on and no way to undo
        // it, and a change with no way back is exactly what this must not make.
        if (Run(rootPath, "rev-parse", "--is-inside-work-tree") is not { Ok: true, Output: var tree }
            || tree.Trim() != "true")
        {
            return Refused(
                $"{rootPath} is not a git work tree. Without one there is no branch to put "
                + "this on and no way to undo it, so it is refused rather than written.");
        }

        // A dirty tree means the commit would carry someone else's work in
        // progress, and the branch would stop being reviewable as one change.
        var status = Run(rootPath, "status", "--porcelain");
        if (status.Ok && status.Output.Trim().Length > 0)
        {
            var changed = status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

            return Refused(
                $"{changed} file(s) have uncommitted changes. Applying now would put them in "
                + "the same commit as this patch, and the branch would stop being one "
                + "reviewable change. Commit or stash them first.");
        }

        var original = Run(rootPath, "rev-parse", "--abbrev-ref", "HEAD");
        if (!original.Ok) return Refused("Could not read the current branch.");

        var from = original.Output.Trim();
        var branch = $"legacy-lens/{kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

        var patchFile = Path.Combine(Path.GetTempPath(), $"lens-{Guid.NewGuid():N}.patch");
        File.WriteAllText(patchFile, patch, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            // Checked before anything is created. A patch that will not apply
            // should leave no branch behind to clean up.
            var check = Run(rootPath, "apply", "--check", patchFile);
            if (!check.Ok)
            {
                return Refused(
                    "git will not apply this patch: " + First(check.Error),
                    "Nothing was created. The patch was generated against a different state "
                    + "of these files, so regenerate it and read it again.");
            }

            if (!Run(rootPath, "checkout", "-b", branch).Ok)
                return Refused($"Could not create the branch {branch}.");

            if (!Run(rootPath, "apply", patchFile).Ok)
            {
                Undo(rootPath, from, branch);
                return Refused("The patch passed its check and then failed to apply.");
            }

            var files = CountChanged(rootPath);

            if (!Run(rootPath, "add", "-A").Ok || !Commit(rootPath, kind, files))
            {
                Undo(rootPath, from, branch);
                return Refused("Could not commit the change.");
            }

            var commit = Run(rootPath, "rev-parse", "--short", "HEAD").Output.Trim();

            // Back where the reader was. Leaving them on a branch they did not
            // choose is the kind of surprise that makes people stop trusting a
            // tool with their repository.
            Run(rootPath, "checkout", from);

            _log.LogInformation("Applied {Kind} to {Branch} as {Commit}", kind, branch, commit);

            return new Landed(true, branch, commit, files, [],
            [
                $"Committed to {branch}, and you are back on {from}.",
                $"Read it with: git diff {from}..{branch}",
                $"Keep it with: git merge {branch}",
                $"Drop it with: git branch -D {branch}",
                "Nothing was pushed. Sending this anywhere is your call, not the tool's.",
            ]);
        }
        finally
        {
            try { File.Delete(patchFile); } catch (IOException) { /* a temp file */ }
        }

        Landed Refused(params string[] why) => new(false, null, null, 0, why, []);
    }

    private bool Commit(string rootPath, string kind, int files)
    {
        var subject = kind switch
        {
            "packages" => "Move package declarations into the project files",
            "sdk" => "Convert project files to the SDK format",
            "versions" => "Bring each package to one version",
            "config" => "Carry configuration into appsettings.json",
            _ => $"Apply the {kind} conversion",
        };

        var body =
            $"{files} file(s), from a patch Legacy Lens generated and a person read.\n\n"
            + "Nothing here was inferred: every value written was read from these files.\n"
            + "The caveats printed beside the patch are the ones that apply.";

        return Run(rootPath, "commit", "-m", subject, "-m", body).Ok;
    }

    private int CountChanged(string rootPath)
    {
        var status = Run(rootPath, "status", "--porcelain");
        return status.Ok
            ? status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;
    }

    /// <summary>
    /// Puts the repository back as it was found.
    ///
    /// Half an application left behind is worse than a refusal: it looks like
    /// the tool succeeded until somebody builds.
    /// </summary>
    private void Undo(string rootPath, string from, string branch)
    {
        Run(rootPath, "reset", "--hard");
        Run(rootPath, "checkout", from);
        Run(rootPath, "branch", "-D", branch);
    }

    private static string First(string error)
    {
        var line = error.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line?.Trim() ?? "no reason given";
    }

    private (bool Ok, string Output, string Error) Run(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        // A commit needs an author, and a machine that has never had one
        // configured fails here rather than at the useful part.
        start.Environment["GIT_AUTHOR_NAME"] = "Legacy Lens";
        start.Environment["GIT_AUTHOR_EMAIL"] = "legacy-lens@localhost";
        start.Environment["GIT_COMMITTER_NAME"] = "Legacy Lens";
        start.Environment["GIT_COMMITTER_EMAIL"] = "legacy-lens@localhost";

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("git did not start.");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);

            return (process.ExitCode == 0, output, error);
        }
        catch (Exception failure)
        {
            return (false, string.Empty, failure.Message);
        }
    }
}
