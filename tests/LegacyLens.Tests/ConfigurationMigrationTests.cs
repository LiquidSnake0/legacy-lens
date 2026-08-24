using System.Diagnostics;
using System.Text;
using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The settings convert; the call sites do not.
///
/// What is worth testing is the boundary between those two, and the finding
/// that falls out of crossing it: keys the code reads that no config file
/// declares, which are nulls the application already meets at runtime.
/// </summary>
public class ConfigurationMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-config-{Guid.NewGuid():N}");

    public ConfigurationMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void WriteConfig(string name, string body)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
            {body}
            </configuration>
            """);
    }

    private void WriteSource(string name, string body)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
    }

    private ConfigurationSurvey Survey() => new ConfigurationMigration().Survey(_root);

    /* ---- reading the settings ---- */

    [Fact]
    public void App_settings_and_connection_strings_are_both_read()
    {
        WriteConfig("Web.config", """
              <appSettings>
                <add key="Mail.Host" value="smtp.example" />
              </appSettings>
              <connectionStrings>
                <add name="Billing" connectionString="Server=db;Database=billing" />
              </connectionStrings>
            """);

        var survey = Survey();

        Assert.Equal("smtp.example", survey.AllAppSettings["Mail.Host"]);
        Assert.Equal("Server=db;Database=billing", survey.AllConnectionStrings["Billing"]);
    }

    [Fact]
    public void A_config_file_that_declares_nothing_is_not_counted()
    {
        WriteConfig("Web.config", "  <system.web />");

        Assert.Empty(Survey().Files);
    }

    [Fact]
    public void A_malformed_config_stops_that_file_rather_than_the_run()
    {
        File.WriteAllText(Path.Combine(_root, "Web.config"), "<configuration><appSettings>");
        WriteConfig("Sub/App.config", """
              <appSettings>
                <add key="Kept" value="yes" />
              </appSettings>
            """);

        Assert.Equal("yes", Survey().AllAppSettings["Kept"]);
    }

    /* ---- reading the call sites ---- */

    [Fact]
    public void A_literal_key_is_recorded_with_the_type_that_reads_it()
    {
        WriteSource("Mailer.cs", """
            using System.Configuration;

            public class Mailer
            {
                public string Host() => ConfigurationManager.AppSettings["Mail.Host"];
            }
            """);

        var read = Assert.Single(Survey().Reads);

        Assert.Equal("Mailer", read.Type);
        Assert.Equal("AppSettings", read.Kind);
        Assert.Equal("Mail.Host", read.Key);
        Assert.True(read.Literal);
    }

    [Fact]
    public void A_fully_qualified_call_is_found_too()
    {
        // The same miss that let System.DateTime.Now through the seam
        // detector once: the owner is a member access, not an identifier.
        WriteSource("Mailer.cs", """
            public class Mailer
            {
                public string Host() =>
                    System.Configuration.ConfigurationManager.AppSettings["Mail.Host"];
            }
            """);

        Assert.Single(Survey().Reads);
    }

    [Fact]
    public void A_computed_key_is_reported_rather_than_guessed()
    {
        WriteSource("Mailer.cs", """
            using System.Configuration;

            public class Mailer
            {
                public string Get(string name) => ConfigurationManager.AppSettings[name];
            }
            """);

        var survey = Survey();

        Assert.Single(survey.Computed);
        Assert.False(survey.Reads[0].Literal);

        // And it is not counted as undeclared: nothing is known about it.
        Assert.Empty(survey.Undeclared);
    }

    [Fact]
    public void A_key_the_code_reads_and_nothing_declares_is_the_finding()
    {
        WriteConfig("Web.config", """
              <appSettings>
                <add key="Mail.Host" value="smtp.example" />
              </appSettings>
            """);

        WriteSource("Mailer.cs", """
            using System.Configuration;

            public class Mailer
            {
                public string Host() => ConfigurationManager.AppSettings["Mail.Host"];
                public string Port() => ConfigurationManager.AppSettings["Mail.Port"];
            }
            """);

        var undeclared = Assert.Single(Survey().Undeclared);

        Assert.Equal("Mail.Port", undeclared.Key);
        Assert.Equal(6, undeclared.Line);
    }

    [Fact]
    public void A_key_declared_and_never_read_is_reported_as_dead_weight()
    {
        WriteConfig("Web.config", """
              <appSettings>
                <add key="Used" value="1" />
                <add key="Forgotten" value="2" />
              </appSettings>
            """);

        WriteSource("Reader.cs", """
            using System.Configuration;

            public class Reader
            {
                public string Get() => ConfigurationManager.AppSettings["Used"];
            }
            """);

        Assert.Equal(["Forgotten"], Survey().Unread);
    }

    /* ---- the patch ---- */

    [Fact]
    public void Nothing_is_proposed_when_there_is_nothing_to_carry_over()
    {
        Assert.Null(new ConfigurationMigration().Propose(Survey(), _root));
    }

    [Fact]
    public void A_dotted_key_is_kept_exactly_as_it_was_written()
    {
        // Nesting it reads better and renames it. .NET joins nested names with
        // a colon, so { "Mail": { "Host": x } } is the key "Mail:Host", and
        // every call site reading "Mail.Host" would get null. Flat, the reads
        // keep working, which is what a translation is for.
        WriteConfig("Web.config", """
              <appSettings>
                <add key="Mail.Host" value="smtp.example" />
              </appSettings>
            """);

        var proposal = new ConfigurationMigration().Propose(Survey(), _root)!;

        Assert.Contains("\"Mail.Host\": \"smtp.example\"", proposal.Patch);
        Assert.DoesNotContain("\"Mail\": {", proposal.Patch);
        Assert.Contains(proposal.Caveats, c => c.Contains("joins nested names with a colon"));
    }

    [Fact]
    public void A_colon_key_is_kept_as_it_was_too()
    {
        // The same argument, and this one is already a .NET configuration path,
        // so a flat property produces exactly the key it always had.
        WriteConfig("Web.config", """
              <appSettings>
                <add key="webpages:Enabled" value="false" />
              </appSettings>
            """);

        Assert.Contains(
            "\"webpages:Enabled\": \"false\"",
            new ConfigurationMigration().Propose(Survey(), _root)!.Patch);
    }

    [Fact]
    public void Two_keys_sharing_a_prefix_both_survive()
    {
        // Nesting these would make one of them delete the other, and losing a
        // setting silently is the failure this conversion exists to avoid.
        WriteConfig("Web.config", """
              <appSettings>
                <add key="Mail" value="on" />
                <add key="Mail.Host" value="smtp.example" />
              </appSettings>
            """);

        var patch = new ConfigurationMigration().Propose(Survey(), _root)!.Patch;

        Assert.Contains("\"Mail\": \"on\"", patch);
        Assert.Contains("\"Mail.Host\": \"smtp.example\"", patch);
    }

    [Fact]
    public void Connection_strings_land_where_dotnet_looks_for_them()
    {
        WriteConfig("Web.config", """
              <connectionStrings>
                <add name="Billing" connectionString="Server=db;Database=billing" />
              </connectionStrings>
            """);

        var patch = new ConfigurationMigration().Propose(Survey(), _root)!.Patch;

        Assert.Contains("\"ConnectionStrings\"", patch);
        Assert.Contains("\"Billing\": \"Server=db;Database=billing\"", patch);
    }

    [Fact]
    public void A_password_in_a_connection_string_is_carried_over_and_flagged()
    {
        WriteConfig("Web.config", """
              <connectionStrings>
                <add name="Billing" connectionString="Server=db;User Id=sa;Password=hunter2" />
              </connectionStrings>
            """);

        var proposal = new ConfigurationMigration().Propose(Survey(), _root)!;

        // Copied, because a translation that edits its source is a different
        // file. Flagged, because it is about to be written to a new one.
        Assert.Contains("hunter2", proposal.Patch);
        Assert.Contains(proposal.Caveats, c => c.Contains("user secrets"));
    }

    [Fact]
    public void A_key_declared_twice_with_two_values_is_named_rather_than_silently_merged()
    {
        WriteConfig("A/Web.config", """
              <appSettings>
                <add key="Mode" value="staging" />
              </appSettings>
            """);

        WriteConfig("B/Web.config", """
              <appSettings>
                <add key="Mode" value="production" />
              </appSettings>
            """);

        var proposal = new ConfigurationMigration().Propose(Survey(), _root)!;

        Assert.Contains(proposal.Caveats, c => c.Contains("more than one config file"));
        Assert.Contains(proposal.Caveats, c => c.Contains("Mode"));
    }

    [Fact]
    public void An_undeclared_key_is_not_invented_into_the_new_file()
    {
        WriteConfig("Web.config", """
              <appSettings>
                <add key="Mail.Host" value="smtp.example" />
              </appSettings>
            """);

        WriteSource("Mailer.cs", """
            using System.Configuration;

            public class Mailer
            {
                public string Port() => ConfigurationManager.AppSettings["Mail.Port"];
            }
            """);

        var proposal = new ConfigurationMigration().Propose(Survey(), _root)!;

        Assert.DoesNotContain("\"Port\"", proposal.Patch);
        Assert.Contains(proposal.Caveats, c => c.Contains("declared") && c.Contains("nowhere"));
    }

    [Fact]
    public void The_verdict_says_why_the_call_sites_are_not_rewritten()
    {
        Assert.Contains("no constructor", ConfigurationMigration.Verdict(isStatic: true));
        Assert.Contains("every caller", ConfigurationMigration.Verdict(isStatic: false));
    }

    [Fact]
    public void Git_accepts_the_patch()
    {
        if (!Run("git", "--version").Ok) return;

        WriteConfig("Web.config", """
              <appSettings>
                <add key="Mail.Host" value="smtp.example" />
                <add key="Timeout" value="30" />
              </appSettings>
              <connectionStrings>
                <add name="Billing" connectionString="Server=db;Database=billing" />
              </connectionStrings>
            """);

        var proposal = new ConfigurationMigration().Propose(Survey(), _root);
        Assert.NotNull(proposal);

        Assert.True(Run("git", "init").Ok);
        Assert.True(Run("git", "add -A").Ok);

        var patchPath = Path.Combine(_root, "config.patch");
        File.WriteAllText(patchPath, proposal.Patch, new UTF8Encoding(false));

        var check = Run("git", $"apply --check \"{patchPath}\"");
        Assert.True(check.Ok, check.Error);
    }

    [Fact]
    public void The_generated_file_is_valid_json()
    {
        WriteConfig("Web.config", """
              <appSettings>
                <add key="Url" value="https://example.test/a?b=1&amp;c=2" />
              </appSettings>
            """);

        var patch = new ConfigurationMigration().Propose(Survey(), _root)!.Patch;

        var json = string.Join('\n', patch
            .Split('\n')
            .SkipWhile(line => !line.StartsWith("@@"))
            .Skip(1)
            .Where(line => line.StartsWith('+'))
            .Select(line => line[1..]));

        var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(
            "https://example.test/a?b=1&c=2",
            parsed.RootElement.GetProperty("Url").GetString());
    }

    private (bool Ok, string Error) Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = _root,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;

            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode == 0, error);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }
}
