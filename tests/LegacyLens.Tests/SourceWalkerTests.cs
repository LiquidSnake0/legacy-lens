using LegacyLens.Api.Ingestion;

namespace LegacyLens.Tests;

/// <summary>
/// SourceWalker is already implemented, these pass from the start and exist to
/// catch a regression in the skip rules, which are the difference between an
/// index of your project and an index of everyone else's.
/// </summary>
public class SourceWalkerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "legacylens-tests-" + Guid.NewGuid().ToString("N"));

    public SourceWalkerTests()
    {
        Write("src/Service.cs", "public class Service { }");
        Write("src/app.component.ts", "export class AppComponent { }");
        Write("README.md", "# project");
        Write("node_modules/left-pad/index.js", "module.exports = 1;");
        Write("bin/Debug/Service.dll", "binary-ish");
        Write("obj/project.assets.json", "{}");
        Write(".git/config", "[core]");
        Write("logo.png", "not really a png");
        Write("empty.cs", "");
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private List<string> Walk() =>
        new SourceWalker().Walk(_root).Select(p => Path.GetRelativePath(_root, p)).ToList();

    [Fact]
    public void Finds_source_files()
    {
        var found = Walk();
        Assert.Contains(Path.Combine("src", "Service.cs"), found);
        Assert.Contains(Path.Combine("src", "app.component.ts"), found);
        Assert.Contains("README.md", found);
    }

    [Fact]
    public void Skips_dependency_and_build_directories()
    {
        var found = Walk();
        Assert.DoesNotContain(found, p => p.Contains("node_modules"));
        Assert.DoesNotContain(found, p => p.Contains("bin"));
        Assert.DoesNotContain(found, p => p.Contains("obj"));
        Assert.DoesNotContain(found, p => p.Contains(".git"));
    }

    [Fact]
    public void Skips_files_that_are_not_source()
    {
        Assert.DoesNotContain("logo.png", Walk());
    }

    [Fact]
    public void Skips_empty_files()
    {
        Assert.DoesNotContain("empty.cs", Walk());
    }

    [Fact]
    public void Missing_directory_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            new SourceWalker().Walk("/no/such/place").ToList());
    }

    [Fact]
    public void Detects_binary_content()
    {
        Assert.True(SourceWalker.LooksBinary(new byte[] { 0x89, 0x50, 0x00, 0x4E }));
        Assert.False(SourceWalker.LooksBinary("public class A { }"u8));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
