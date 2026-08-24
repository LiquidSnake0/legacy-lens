namespace LegacyLens.Api;

/// <summary>
/// Whether this process is allowed to run code it was handed.
///
/// Everything else in this tool reads. It parses files that do not build, it
/// compiles a rewrite without running it, and none of that can do anything to
/// the machine it runs on. Comparing behaviour is the one capability that has
/// to execute somebody else's code, and on a rewrite the model wrote, that is
/// executing something nobody has read yet.
///
/// So it is off unless the operator turned it on. Not a warning, not a
/// confirm dialog: a setting on the process, which is the only place a decision
/// like this can be made honestly, because the person who deploys is the person
/// who knows whose code this is.
///
/// The published image does not set it. A demo anybody can reach cannot be
/// talked into running anything, and that is the property worth keeping.
/// </summary>
public class Execution
{
    public const string Setting = "ALLOW_RUNNING_CODE";

    private readonly bool _allowed;

    public Execution(IConfiguration configuration)
    {
        var value = configuration[Setting];

        _allowed = value is not null
                   && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }

    public bool Allowed => _allowed;

    /// <summary>What to tell somebody who asked for it and did not get it.</summary>
    public string Refusal =>
        $"Behaviour was not checked because this server does not run code it was handed. "
      + $"Comparing two versions means calling both, and on a rewrite a model wrote that is "
      + $"executing something nobody has read. Set {Setting}=true on the server if the code "
      + $"is yours and you would run it yourself, or run it from the command line instead: "
      + $"`dotnet run --project src/LegacyLens.Api -- equivalence <before.cs> <after.cs>`.";
}
