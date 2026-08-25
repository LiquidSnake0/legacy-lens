using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Which files an analysis looks at, decided once.
///
/// Seven classes walked a directory tree, each with its own copy of the same
/// list of folders to skip. The copies were identical, which is what made them
/// worth pulling out: the day one of them gains a folder, two analyses of the
/// same solution count different files and the report contradicts itself with
/// no error anywhere.
/// </summary>
public class SourceTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-tree-{Guid.NewGuid():N}");

    public SourceTreeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string relative, string content = "// x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private IReadOnlyList<string> Found(IEnumerable<string> paths) =>
        paths.Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/'))
             .OrderBy(p => p, StringComparer.Ordinal)
             .ToList();

    [Fact]
    public void Build_output_and_fetched_dependencies_are_not_this_codebase()
    {
        // Not an optimisation. A solution's `packages` folder holds other
        // people's code, and counting it would make every measurement here a
        // measurement of somebody else's work.
        Write("src/Real.cs");
        Write("src/bin/Debug/Real.cs");
        Write("src/obj/Generated.cs");
        Write("packages/Someone.Else/Their.cs");
        Write("node_modules/thing/index.cs");
        Write(".git/hooks/hook.cs");

        Assert.Equal(["src/Real.cs"], Found(SourceTree.CSharpUnder(_root)));
    }

    [Fact]
    public void A_folder_named_like_a_skipped_one_deeper_down_is_skipped_too()
    {
        Write("src/Keep.cs");
        Write("src/Project/obj/Debug/Temp.cs");

        Assert.Equal(["src/Keep.cs"], Found(SourceTree.CSharpUnder(_root)));
    }

    [Fact]
    public void The_caller_decides_which_files_it_wants()
    {
        Write("src/A.cs");
        Write("src/A.csproj", "<Project />");
        Write("src/web.config", "<configuration />");

        Assert.Equal(
            ["src/A.csproj", "src/web.config"],
            Found(SourceTree.Under(_root, p => !p.EndsWith(".cs", StringComparison.Ordinal))));
    }

    [Fact]
    public void Nothing_under_an_empty_folder_is_not_an_error()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        Assert.Empty(SourceTree.CSharpUnder(_root));
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_ends_that_branch_and_not_the_walk()
    {
        // A permission a reader does not have is a fact about the machine. An
        // analysis of ninety projects should not stop at the one folder it was
        // not allowed to open.
        Write("src/Reachable.cs");

        var closed = Path.Combine(_root, "closed");
        Directory.CreateDirectory(closed);
        File.WriteAllText(Path.Combine(closed, "Hidden.cs"), "// x");

        try
        {
            File.SetUnixFileMode(closed, UnixFileMode.None);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
        {
            return;
        }

        try
        {
            // Running as root reads it anyway, and the walk is still expected
            // to include what it could reach either way.
            Assert.Contains("src/Reachable.cs", Found(SourceTree.CSharpUnder(_root)));
        }
        finally
        {
            File.SetUnixFileMode(closed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void The_skip_list_is_one_list_rather_than_seven()
    {
        // The reason this class exists. Asserted so that a copy reappearing
        // somewhere has to disagree with this to survive.
        Assert.Contains("bin", SourceTree.Skip);
        Assert.Contains("obj", SourceTree.Skip);
        Assert.Contains("packages", SourceTree.Skip);
        Assert.True(SourceTree.Skipped(Path.Combine(_root, "BIN")));
        Assert.False(SourceTree.Skipped(Path.Combine(_root, "src")));
    }
}
