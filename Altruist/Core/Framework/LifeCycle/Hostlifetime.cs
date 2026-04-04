namespace Altruist;

using Microsoft.Extensions.Hosting;

using System.Threading;

[Service(typeof(IHostApplicationLifetime))]
public sealed class AltruistHostLifetime : IHostApplicationLifetime, IDisposable
{
    public CancellationTokenSource StartedSource { get; } = new();
    public CancellationTokenSource StoppingSource { get; } = new();
    public CancellationTokenSource StoppedSource { get; } = new();

    public CancellationToken ApplicationStarted => StartedSource.Token;
    public CancellationToken ApplicationStopping => StoppingSource.Token;
    public CancellationToken ApplicationStopped => StoppedSource.Token;

    public void StopApplication() => StoppingSource.Cancel();

    public void TriggerStarted() => StartedSource.Cancel();
    public void TriggerStopped() => StoppedSource.Cancel();

    public void Dispose()
    {
        StartedSource.Dispose();
        StoppingSource.Dispose();
        StoppedSource.Dispose();
    }
}
