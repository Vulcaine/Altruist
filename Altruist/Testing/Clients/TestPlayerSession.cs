/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Configuration;

namespace Altruist.Testing;

/// <summary>
/// High-level helper that drives the full new-player flow: signup → login →
/// create-character → game/join → TCP connect → upgrade → enter-world. Exposes
/// the resulting <see cref="Http"/> + <see cref="Tcp"/> clients so tests can keep
/// driving them.
///
/// <code>
/// public CombatTest(TestPlayerSession session) { _session = session; }
///
/// public Task InitializeAsync() => _session.SetupPlayerAsync(race: 0);
///
/// [Fact]
/// public async Task Attacks() {
///     await _session.Tcp.SendAsync("attack", new CAttack { VictimVID = 10001 });
/// }
/// </code>
///
/// <para>Each ctor param of <see cref="TestPlayerSession"/> gets its own pair of
/// HTTP + TCP clients, so multi-player scenarios (party invite, exchange) just
/// take two parameters.</para>
///
/// <para>The endpoints invoked here come from Valeria's HTTP login surface
/// (<c>/api/v1/auth/signup</c>, <c>/api/v1/auth/login/unamepwd</c>,
/// <c>/api/v1/characters</c>, <c>/api/v1/game/join</c>,
/// <c>/api/v1/auth/upgrade</c>). Other Altruist-based games using a different
/// auth surface should drive the flow with raw <see cref="TestHttpClient"/> +
/// <see cref="TestTcpClient"/> instead.</para>
/// </summary>
public sealed class TestPlayerSession : IAsyncDisposable
{
    public TestHttpClient Http { get; }
    public TestTcpClient Tcp { get; }

    public string? Username { get; private set; }
    public string? Password { get; private set; }
    public string? Jwt { get; private set; }
    public string? CharacterName { get; private set; }
    public int CharX { get; private set; }
    public int CharY { get; private set; }
    public int CharZ { get; private set; }

    public TestPlayerSession(IConfiguration cfg)
    {
        Http = new TestHttpClient(cfg);
        Tcp = new TestTcpClient(cfg);
    }

    /// <summary>Run the full new-player flow. After this returns, the player is
    /// in-world and both <see cref="Http"/> + <see cref="Tcp"/> can be driven
    /// directly.</summary>
    public async Task SetupPlayerAsync(
        int race = 0, int empire = 1,
        int str = 6, int con = 4, int dex = 3, int intel = 3,
        string? characterName = null,
        string? username = null,
        string password = "pass123",
        CancellationToken ct = default)
    {
        Username = username ?? $"t_{Guid.NewGuid():N}"[..16];
        Password = password;

        var signup = await Http.PostAsJsonAsync("/api/v1/auth/signup",
            new { email = $"{Username}@test.dev", username = Username, password }, ct);
        if (!signup.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Signup failed ({(int)signup.StatusCode}): {await signup.Content.ReadAsStringAsync(ct)}");

        Jwt = await LoginAsync(Username!, password, ct);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Jwt);

        CharacterName = characterName ?? $"P{race}_{Username[2..7]}";
        var create = await Http.PostAsJsonAsync("/api/v1/characters",
            new { name = CharacterName, race, empire, shape = 0, str, con, dex, @int = intel }, ct);
        if (!create.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Create-character failed ({(int)create.StatusCode}): {await create.Content.ReadAsStringAsync(ct)}");

        var join = await Http.PostAsJsonAsync("/api/v1/game/join", new { characterIndex = 0 }, ct);
        if (!join.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"game/join failed ({(int)join.StatusCode}): {await join.Content.ReadAsStringAsync(ct)}");

        var gameState = JsonDocument.Parse(await join.Content.ReadAsStringAsync(ct));
        var character = gameState.RootElement.GetProperty("character");
        CharX = character.GetProperty("x").GetInt32();
        CharY = character.GetProperty("y").GetInt32();
        if (character.TryGetProperty("z", out var z)) CharZ = z.GetInt32();

        await Tcp.ConnectAsync(ct);

        var upgrade = await Http.PostAsJsonAsync("/api/v1/auth/upgrade",
            new { token = Jwt, clientId = Tcp.ClientId }, ct);
        if (!upgrade.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"auth/upgrade failed ({(int)upgrade.StatusCode}): {await upgrade.Content.ReadAsStringAsync(ct)}");

        // Send enter-world. Payload is the empty CEnterGame; we don't take a
        // typed reference to it here (Altruist.Testing must not depend on the
        // game project), so callers using a custom enter-world packet can call
        // Tcp.SendAsync directly. For Valeria the server accepts a null body.
        await Tcp.SendAsync("enter-world", payload: null, ct);
        await Task.Delay(2000, ct);
        await Tcp.DrainAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>Resume an existing account. Skips signup, logs in fresh,
    /// joins with the existing character (index 0), completes the TCP
    /// handshake. Used to test logout-login round-trips.</summary>
    public async Task ResumeAsync(string username, string password = "pass123", int characterIndex = 0, CancellationToken ct = default)
    {
        Username = username;
        Password = password;
        Jwt = await LoginAsync(username, password, ct);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Jwt);

        var join = await Http.PostAsJsonAsync("/api/v1/game/join", new { characterIndex }, ct);
        if (!join.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"game/join failed: {await join.Content.ReadAsStringAsync(ct)}");

        var gameState = JsonDocument.Parse(await join.Content.ReadAsStringAsync(ct));
        var character = gameState.RootElement.GetProperty("character");
        CharacterName = character.GetProperty("name").GetString();

        await Tcp.ConnectAsync(ct);

        var upgrade = await Http.PostAsJsonAsync("/api/v1/auth/upgrade",
            new { token = Jwt, clientId = Tcp.ClientId }, ct);
        if (!upgrade.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"auth/upgrade failed: {await upgrade.Content.ReadAsStringAsync(ct)}");

        await Tcp.SendAsync("enter-world", payload: null, ct);
        await Task.Delay(2000, ct);
        await Tcp.DrainAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>Disconnect the TCP and wait for the despawn save path to commit
    /// to Postgres. Without the wait, assertions race against the autosave
    /// flush.</summary>
    public async Task DisconnectAndFlushAsync(TimeSpan flushDelay)
    {
        Tcp.Dispose();
        await Task.Delay(flushDelay);
    }

    private async Task<string> LoginAsync(string username, string password, CancellationToken ct)
    {
        var login = await Http.PostAsJsonAsync("/api/v1/auth/login/unamepwd",
            new { username, password }, ct);
        if (!login.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Login failed ({(int)login.StatusCode}): {await login.Content.ReadAsStringAsync(ct)}");

        var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync(ct));
        var raw = doc.RootElement.GetProperty("accessToken").GetString()!;
        return raw.Contains(';') ? raw[..raw.IndexOf(';')] : raw;
    }

    public async ValueTask DisposeAsync()
    {
        try { Http.Dispose(); } catch { }
        try { await Tcp.DisposeAsync(); } catch { }
    }
}
