/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Networking;

namespace Tests.Altruist.Framework.Networking;

public sealed class PairingSessionsTests
{
    private static PairingSessions Sessions(int maxCompanions = 2, int maxSessions = 10, params string[] codes)
    {
        var queue = new Queue<string>(codes.Length > 0 ? codes : new[] { "ABC123", "XYZ789", "QWE456" });
        return PairingSessions.Create(new PairingOptions { MaxCompanions = maxCompanions, MaxSessions = maxSessions, MaxPayloadBytes = 8 }, _ => queue.Dequeue());
    }

    [Fact]
    public void a_host_gets_a_code_and_companions_join_it_in_slot_order()
    {
        var s = Sessions();
        Assert.Equal("ABC123", s.Host("tv").Value);

        Assert.Equal(new PairingJoin(1, "tv"), s.Join("phone-a", "abc123").Value);
        Assert.Equal(new PairingJoin(2, "tv"), s.Join("phone-b", " ABC123 ").Value);
    }

    [Fact]
    public void a_full_session_and_an_unknown_code_are_refused()
    {
        var s = Sessions(maxCompanions: 1);
        s.Host("tv");
        s.Join("phone-a", "ABC123");

        Assert.Equal(PairingRejectReason.Full, s.Join("phone-b", "ABC123").Reject);
        Assert.Equal(PairingRejectReason.UnknownCode, s.Join("phone-c", "NOPE00").Reject);
    }

    [Fact]
    public void a_connection_pairs_once()
    {
        var s = Sessions();
        s.Host("tv");
        s.Join("phone", "ABC123");

        Assert.Equal(PairingRejectReason.AlreadyPaired, s.Host("tv").Reject);
        Assert.Equal(PairingRejectReason.AlreadyPaired, s.Host("phone").Reject);
        Assert.Equal(PairingRejectReason.AlreadyPaired, s.Join("phone", "ABC123").Reject);
    }

    [Fact]
    public void a_code_in_use_is_drawn_again_and_the_session_cap_holds()
    {
        var s = Sessions(maxSessions: 2, codes: new[] { "AAAAAA", "AAAAAA", "BBBBBB" });
        Assert.Equal("AAAAAA", s.Host("tv-1").Value);
        Assert.Equal("BBBBBB", s.Host("tv-2").Value);
        Assert.Equal(PairingRejectReason.TooManySessions, s.Host("tv-3").Reject);
    }

    [Fact]
    public void the_host_reaches_any_companion_and_companions_reach_only_the_host()
    {
        var s = Sessions();
        s.Host("tv");
        s.Join("phone-a", "ABC123");
        s.Join("phone-b", "ABC123");

        Assert.Equal(new PairingRoute("phone-b", 0), s.Route("tv", 2, 4).Value);
        Assert.Equal(new PairingRoute("tv", 1), s.Route("phone-a", 0, 4).Value);
        Assert.Equal(PairingRejectReason.NoSuchPeer, s.Route("phone-a", 2, 4).Reject);
        Assert.Equal(PairingRejectReason.NoSuchPeer, s.Route("tv", 0, 4).Reject);
        Assert.Equal(PairingRejectReason.NoSuchPeer, s.Route("tv", 3, 4).Reject);
        Assert.Equal(PairingRejectReason.NoSuchPeer, s.Route("stranger", 0, 4).Reject);
    }

    [Fact]
    public void an_oversized_payload_is_refused()
    {
        var s = Sessions();
        s.Host("tv");
        s.Join("phone", "ABC123");
        Assert.Equal(PairingRejectReason.PayloadTooLarge, s.Route("phone", 0, 9).Reject);
    }

    [Fact]
    public void a_companion_leaving_frees_its_slot_and_tells_the_host()
    {
        var s = Sessions();
        s.Host("tv");
        s.Join("phone-a", "ABC123");
        s.Join("phone-b", "ABC123");

        Assert.Equal(new[] { ("tv", 1) }, s.Leave("phone-a").Notify);
        Assert.Equal(1, s.Join("phone-c", "ABC123").Value.Slot);
    }

    [Fact]
    public void the_host_leaving_closes_the_session_for_every_companion()
    {
        var s = Sessions();
        s.Host("tv");
        s.Join("phone-a", "ABC123");
        s.Join("phone-b", "ABC123");

        Assert.Equal(new[] { ("phone-a", 0), ("phone-b", 0) }, s.Leave("tv").Notify.OrderBy(n => n.ClientId));
        Assert.Equal(0, s.Count);
        Assert.Equal(PairingRejectReason.UnknownCode, s.Join("phone-c", "ABC123").Reject);
        Assert.Empty(s.Leave("phone-a").Notify);
        Assert.Equal("XYZ789", s.Host("phone-a").Value);
    }

    [Fact]
    public void unknown_connections_leave_nobody_to_tell()
    {
        Assert.Empty(Sessions().Leave("stranger").Notify);
    }

    [Fact]
    public void random_codes_use_the_alphabet()
    {
        var s = new PairingSessions();
        var code = s.Host("tv").Value!;
        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.Contains(c, PairingSessions.CodeAlphabet));
    }
}
