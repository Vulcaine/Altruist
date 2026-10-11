namespace Altruist;

/// <summary>
/// Command-line arguments of the current process (without the executable path); <c>Args</c> is
/// <see cref="Environment.GetCommandLineArgs"/> minus the first entry.
/// </summary>
public interface IApplicationArgs
{
    /// <summary>The command-line arguments, without the executable path.</summary>
    string[] Args { get; }
}

/// <summary>
/// Singleton service exposing the process's command-line arguments. Registered under its own type only
/// (inject <see cref="ApplicationArgs"/>, not <see cref="IApplicationArgs"/>). To read arguments as configuration
/// values use <see cref="AppConfigValueAttribute"/> instead.
/// </summary>
[Service]
public sealed class ApplicationArgs : IApplicationArgs
{
    /// <inheritdoc/>
    public string[] Args { get; }

    /// <summary>Captures <see cref="Environment.GetCommandLineArgs"/> (skipping the executable path).</summary>
    public ApplicationArgs() => Args = Environment.GetCommandLineArgs().Skip(1).ToArray();
}
