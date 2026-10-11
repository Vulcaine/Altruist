/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Security.Cryptography;

namespace Altruist.Networking;

/// <summary>Limits of device pairing (<see cref="PairingSessions"/>, <see cref="PairingPortal"/>).</summary>
public sealed record PairingOptions
{
    /// <summary>Companions one host may pair (slots 1..this). Default 4.</summary>
    public int MaxCompanions { get; init; } = 4;

    /// <summary>Length of a pairing code. Default 6 (about 10^9 codes over <see cref="PairingSessions.CodeAlphabet"/>).</summary>
    public int CodeLength { get; init; } = 6;

    /// <summary>Open sessions on this server; a host over the limit is rejected. Default 10000.</summary>
    public int MaxSessions { get; init; } = 10_000;

    /// <summary>Largest relayed payload in bytes. Default 16384 (a WebRTC offer with its candidates fits).</summary>
    public int MaxPayloadBytes { get; init; } = 16_384;
}

/// <summary>Why a pairing request was refused (sent to the client in <see cref="PairingRejectedPacket"/>).</summary>
public enum PairingRejectReason
{
    /// <summary>No open session has this code.</summary>
    UnknownCode = 1,
    /// <summary>The session already has <see cref="PairingOptions.MaxCompanions"/> companions.</summary>
    Full = 2,
    /// <summary>This connection already hosts or joined a session.</summary>
    AlreadyPaired = 3,
    /// <summary>The server has <see cref="PairingOptions.MaxSessions"/> open sessions.</summary>
    TooManySessions = 4,
    /// <summary>A relayed payload is larger than <see cref="PairingOptions.MaxPayloadBytes"/>.</summary>
    PayloadTooLarge = 5,
    /// <summary>The sender is not in a session, or the target slot is empty.</summary>
    NoSuchPeer = 6,
}

/// <summary>A pairing outcome: the value, or why it was refused.</summary>
/// <typeparam name="T">The value type.</typeparam>
public readonly record struct PairingResult<T>(T? Value, PairingRejectReason? Reject)
{
    /// <summary>A successful result.</summary>
    public static PairingResult<T> Ok(T value) => new(value, null);

    /// <summary>A refusal.</summary>
    public static PairingResult<T> Refused(PairingRejectReason reason) => new(default, reason);
}

/// <summary>A companion that joined: its slot and the host to tell.</summary>
/// <param name="Slot">The companion's slot (1..<see cref="PairingOptions.MaxCompanions"/>).</param>
/// <param name="HostClientId">The host's connection.</param>
public readonly record struct PairingJoin(int Slot, string HostClientId);

/// <summary>Where a relayed payload goes: the target connection and the sender's slot.</summary>
/// <param name="TargetClientId">The receiving connection.</param>
/// <param name="FromSlot">The sender's slot (0 = host).</param>
public readonly record struct PairingRoute(string TargetClientId, int FromSlot);

/// <summary>Who must hear that a connection left its session.</summary>
/// <param name="Notify">Connections to tell, each with the slot that left (0 = the host: the session closed).</param>
public readonly record struct PairingLeave(IReadOnlyList<(string ClientId, int Slot)> Notify)
{
    /// <summary>Nobody to tell (the connection was not paired).</summary>
    public static PairingLeave None { get; } = new(Array.Empty<(string, int)>());
}

/// <summary>
/// Device pairing sessions: a host (e.g. the game on a TV) opens a session and gets a short code;
/// companions (e.g. phones used as controllers) join with the code and get a slot; the session
/// relays payloads between the host (slot 0) and its companions. Closing the host closes the
/// session. Pure bookkeeping, no I/O: <see cref="PairingPortal"/> wires it to connections.
/// </summary>
/// <remarks>
/// Thread-safe (one lock). One instance per process, registered when <c>altruist:server:pairing</c> is
/// configured (<c>max-companions</c>, <c>code-length</c>, <c>max-sessions</c>, <c>max-payload-bytes</c>;
/// defaults as in <see cref="PairingOptions"/>). Sessions live on one server: the host and its
/// companions must reach the same server process. Codes use <see cref="CodeAlphabet"/> (no 0/O, 1/I)
/// and are matched case-insensitively.
/// </remarks>
[Service]
[ConditionalOnConfig("altruist:server:pairing")]
public sealed class PairingSessions
{
    /// <summary>Characters of a pairing code.</summary>
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private sealed class Session
    {
        public required string Code { get; init; }
        public required string HostClientId { get; init; }
        public readonly Dictionary<int, string> Companions = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _byCode = new(StringComparer.Ordinal);
    /// <summary>Every paired connection: its session and slot (0 = host).</summary>
    private readonly Dictionary<string, (Session Session, int Slot)> _byClient = new(StringComparer.Ordinal);
    private readonly Func<int, string> _newCode;

    /// <summary>The limits in force.</summary>
    public PairingOptions Options { get; }

    /// <summary>Creates the registry from <c>altruist:server:pairing</c> (DI).</summary>
    /// <param name="maxCompanions"><c>max-companions</c>.</param>
    /// <param name="codeLength"><c>code-length</c>.</param>
    /// <param name="maxSessions"><c>max-sessions</c>.</param>
    /// <param name="maxPayloadBytes"><c>max-payload-bytes</c>.</param>
    public PairingSessions(
        [AppConfigValue("altruist:server:pairing:max-companions", "4")] int maxCompanions = 4,
        [AppConfigValue("altruist:server:pairing:code-length", "6")] int codeLength = 6,
        [AppConfigValue("altruist:server:pairing:max-sessions", "10000")] int maxSessions = 10_000,
        [AppConfigValue("altruist:server:pairing:max-payload-bytes", "16384")] int maxPayloadBytes = 16_384)
        : this(new PairingOptions { MaxCompanions = maxCompanions, CodeLength = codeLength, MaxSessions = maxSessions, MaxPayloadBytes = maxPayloadBytes }, RandomCode)
    {
    }

    /// <summary>A registry with these limits and random codes, or codes from <paramref name="newCode"/> (tests).</summary>
    /// <param name="options">Limits.</param>
    /// <param name="newCode">Returns a code of the given length; a code in use is drawn again.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is below 1.</exception>
    public static PairingSessions Create(PairingOptions options, Func<int, string>? newCode = null) => new(options, newCode ?? RandomCode);

    private PairingSessions(PairingOptions options, Func<int, string> newCode)
    {
        if (options.MaxCompanions < 1 || options.CodeLength < 1 || options.MaxSessions < 1 || options.MaxPayloadBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Pairing limits must be at least 1.");
        Options = options;
        _newCode = newCode;
    }

    /// <summary>Open sessions.</summary>
    public int Count
    {
        get { lock (_gate) return _byCode.Count; }
    }

    /// <summary>Opens a session hosted by <paramref name="hostClientId"/> and returns its code.</summary>
    public PairingResult<string> Host(string hostClientId)
    {
        lock (_gate)
        {
            if (_byClient.ContainsKey(hostClientId))
                return PairingResult<string>.Refused(PairingRejectReason.AlreadyPaired);
            if (_byCode.Count >= Options.MaxSessions)
                return PairingResult<string>.Refused(PairingRejectReason.TooManySessions);

            string code;
            do
                code = _newCode(Options.CodeLength);
            while (_byCode.ContainsKey(code));

            var session = new Session { Code = code, HostClientId = hostClientId };
            _byCode[code] = session;
            _byClient[hostClientId] = (session, 0);
            return PairingResult<string>.Ok(code);
        }
    }

    /// <summary>Joins <paramref name="clientId"/> to the session with <paramref name="code"/> in its lowest free slot.</summary>
    public PairingResult<PairingJoin> Join(string clientId, string code)
    {
        var key = Normalize(code);
        lock (_gate)
        {
            if (_byClient.ContainsKey(clientId))
                return PairingResult<PairingJoin>.Refused(PairingRejectReason.AlreadyPaired);
            if (!_byCode.TryGetValue(key, out var session))
                return PairingResult<PairingJoin>.Refused(PairingRejectReason.UnknownCode);

            var slot = Enumerable.Range(1, Options.MaxCompanions).FirstOrDefault(s => !session.Companions.ContainsKey(s));
            if (slot == 0)
                return PairingResult<PairingJoin>.Refused(PairingRejectReason.Full);

            session.Companions[slot] = clientId;
            _byClient[clientId] = (session, slot);
            return PairingResult<PairingJoin>.Ok(new PairingJoin(slot, session.HostClientId));
        }
    }

    /// <summary>
    /// Where a payload of <paramref name="payloadBytes"/> from <paramref name="fromClientId"/> to
    /// <paramref name="toSlot"/> goes. The host may reach any companion; a companion only the host.
    /// </summary>
    public PairingResult<PairingRoute> Route(string fromClientId, int toSlot, int payloadBytes)
    {
        if (payloadBytes > Options.MaxPayloadBytes)
            return PairingResult<PairingRoute>.Refused(PairingRejectReason.PayloadTooLarge);
        lock (_gate)
        {
            if (!_byClient.TryGetValue(fromClientId, out var from))
                return PairingResult<PairingRoute>.Refused(PairingRejectReason.NoSuchPeer);
            if (from.Slot != 0 && toSlot != 0)
                return PairingResult<PairingRoute>.Refused(PairingRejectReason.NoSuchPeer);

            var target = toSlot == 0 ? from.Session.HostClientId : from.Session.Companions.GetValueOrDefault(toSlot);
            if (target is null || toSlot == from.Slot)
                return PairingResult<PairingRoute>.Refused(PairingRejectReason.NoSuchPeer);
            return PairingResult<PairingRoute>.Ok(new PairingRoute(target, from.Slot));
        }
    }

    /// <summary>
    /// Removes <paramref name="clientId"/> from its session. A host closes the session (every companion
    /// hears slot 0 left); a companion frees its slot (the host hears it). Unknown ids: nobody.
    /// </summary>
    public PairingLeave Leave(string clientId)
    {
        lock (_gate)
        {
            if (!_byClient.Remove(clientId, out var entry))
                return PairingLeave.None;

            var session = entry.Session;
            if (entry.Slot != 0)
            {
                session.Companions.Remove(entry.Slot);
                return new PairingLeave(new[] { (session.HostClientId, entry.Slot) });
            }

            _byCode.Remove(session.Code);
            var notify = session.Companions.Values.Select(c => (c, 0)).ToList();
            foreach (var companion in session.Companions.Values)
                _byClient.Remove(companion);
            return new PairingLeave(notify);
        }
    }

    /// <summary>Upper-case, trimmed form of a typed code.</summary>
    public static string Normalize(string code) => (code ?? "").Trim().ToUpperInvariant();

    private static string RandomCode(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }
}
