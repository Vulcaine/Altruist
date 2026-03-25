using Altruist;

/// <summary>
/// Dual-transport server: TCP for reliable packets (login, chat, inventory)
/// and UDP for fast unreliable packets (movement, position sync).
///
/// Demonstrates: multi-transport config, per-transport codec, route separation.
/// TCP portal handles login/chat, UDP portal handles position updates.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
        => await AltruistApplication.Run(args);
}
