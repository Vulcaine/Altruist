namespace Altruist;

public interface IApplicationArgs { string[] Args { get; } }

[Service]
public sealed class ApplicationArgs : IApplicationArgs
{
    public string[] Args { get; }
    public ApplicationArgs() => Args = Environment.GetCommandLineArgs().Skip(1).ToArray();
}