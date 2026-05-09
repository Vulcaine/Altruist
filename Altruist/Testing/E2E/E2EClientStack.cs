/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Client;
using Altruist.Client.Inventory;

using Microsoft.Extensions.Configuration;

namespace Altruist.Testing.E2E;

/// <summary>
/// Per-test E2E client stack. Owns one <see cref="TestPlayerSession"/> (which
/// internally owns its own HTTP + TCP clients), one
/// <see cref="ClientPacketDispatcher"/>, and one
/// <see cref="ClientInventoryService"/> — same shape the Unity client builds at
/// boot, but in-process inside the test runner.
///
/// <para>Workflow:
/// <list type="number">
///   <item>Register the game's container layouts on <see cref="Inventory"/>
///   (e.g. <c>new GridLayout(window: 1, cols: 5, rows: 9)</c>).</item>
///   <item>Drive the new-player flow with <c>Session.SetupPlayerAsync(...)</c>
///   — same path the existing <c>[AltruistIntegrationTest]</c> uses.</item>
///   <item>Call <see cref="PumpAsync"/> to drain TCP frames and feed them to the
///   dispatcher; the inventory mirror updates as packets arrive.</item>
///   <item>Assert against <see cref="Inventory"/>, <see cref="Session.Tcp"/>'s
///   <c>DrainAsync</c>, etc.</item>
/// </list>
/// </para>
/// </summary>
public sealed class E2EClientStack : IAsyncDisposable
{
    /// <summary>Drives the full new-player handshake (signup → login →
    /// game/join → TCP → enter-world). Also exposes <c>Session.Http</c> and
    /// <c>Session.Tcp</c> for direct packet driving.</summary>
    public TestPlayerSession Session { get; }

    /// <summary>Client-side dispatcher with <see cref="Inventory"/> already
    /// registered. Add other handlers here if your test needs them.</summary>
    public ClientPacketDispatcher Dispatcher { get; }

    /// <summary>Client-side inventory mirror. Tests must call
    /// <see cref="ClientInventoryService.RegisterContainer"/> with the game's
    /// layouts before driving traffic, since the framework doesn't know your
    /// bag size or equipment slot names.</summary>
    public ClientInventoryService Inventory { get; }

    /// <summary>Codec used by <see cref="Dispatcher"/>. Defaults to
    /// MessagePack — matches the Unity client default.</summary>
    public IClientCodec Codec { get; }

    public E2EClientStack(IConfiguration cfg)
    {
        Session = new TestPlayerSession(cfg);
        Dispatcher = new ClientPacketDispatcher();
        Inventory = new ClientInventoryService();
        Dispatcher.Register(Inventory);
        Codec = new MessagePackClientCodec();
    }

    /// <summary>
    /// Drain inbound TCP frames for up to <paramref name="timeout"/> and feed
    /// each frame into <see cref="Dispatcher"/>. The mirror state reflects
    /// every packet received during the window when this returns. Idempotent —
    /// each call drains whatever has accumulated since the last call.
    /// </summary>
    public async Task PumpAsync(TimeSpan timeout)
    {
        var frames = await Session.Tcp.DrainAsync(timeout).ConfigureAwait(false);
        foreach (var frame in frames)
        {
            try { Dispatcher.Dispatch(frame, Codec); }
            catch { /* per-frame errors already routed via Dispatcher.Logger */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync().ConfigureAwait(false);
    }
}
