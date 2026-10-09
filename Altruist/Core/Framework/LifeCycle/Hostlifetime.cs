namespace Altruist;

using Microsoft.Extensions.Hosting;

using System.Threading;

/// <summary>
/// Altruist's <see cref="IHostApplicationLifetime"/> registration: three cancellation sources that are cancelled to
/// signal started / stopping / stopped. The framework uses <see cref="StopApplication"/> to shut down when required
/// services fail to connect at startup.
/// </summary>
/// <remarks>
/// <see cref="ApplicationStarted"/> and <see cref="ApplicationStopped"/> only fire when <see cref="TriggerStarted"/> /
/// <see cref="TriggerStopped"/> are called; nothing in the framework calls them currently, so don't rely on those two
/// tokens. Inject <see cref="IHostApplicationLifetime"/> rather than this type.
/// </remarks>
[Service(typeof(IHostApplicationLifetime))]
public sealed class AltruistHostLifetime : IHostApplicationLifetime, IDisposable
{
    /// <summary>Source behind <see cref="ApplicationStarted"/>.</summary>
    public CancellationTokenSource StartedSource { get; } = new();
    /// <summary>Source behind <see cref="ApplicationStopping"/>.</summary>
    public CancellationTokenSource StoppingSource { get; } = new();
    /// <summary>Source behind <see cref="ApplicationStopped"/>.</summary>
    public CancellationTokenSource StoppedSource { get; } = new();

    /// <inheritdoc/>
    public CancellationToken ApplicationStarted => StartedSource.Token;
    /// <inheritdoc/>
    public CancellationToken ApplicationStopping => StoppingSource.Token;
    /// <inheritdoc/>
    public CancellationToken ApplicationStopped => StoppedSource.Token;

    /// <summary>Requests shutdown by cancelling <see cref="ApplicationStopping"/>.</summary>
    public void StopApplication() => StoppingSource.Cancel();

    /// <summary>Cancels <see cref="ApplicationStarted"/> (signals that the application has started).</summary>
    public void TriggerStarted() => StartedSource.Cancel();
    /// <summary>Cancels <see cref="ApplicationStopped"/> (signals that shutdown has completed).</summary>
    public void TriggerStopped() => StoppedSource.Cancel();

    /// <summary>Disposes the three cancellation sources.</summary>
    public void Dispose()
    {
        StartedSource.Dispose();
        StoppingSource.Dispose();
        StoppedSource.Dispose();
    }
}
