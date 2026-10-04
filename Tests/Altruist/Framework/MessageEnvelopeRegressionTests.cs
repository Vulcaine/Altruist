/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;

namespace Tests.Altruist.Framework;

/// <summary>
/// MessageEnvelope.Header is a struct property: calling a mutating method on it directly only
/// changed a temporary copy, so Stamp/SetReceiver/SetTimestamp silently did nothing.
/// </summary>
public sealed class MessageEnvelopeRegressionTests
{
    private sealed class Ping : IPacketBase
    {
        public uint MessageCode { get; set; } = 42;
    }

    [Fact]
    public void Stamp_updates_sender_receiver_and_timestamp()
    {
        var envelope = new MessageEnvelope(new Ping(), "nobody");
        var at = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        envelope.Stamp("server-1", "client-9", at);

        Assert.Equal("server-1", envelope.Header.Sender);
        Assert.Equal("client-9", envelope.Header.Receiver);
        Assert.Equal(at.Ticks, envelope.Header.Timestamp);
        Assert.Equal(42u, envelope.MessageCode);
    }

    [Fact]
    public void SetReceiver_updates_the_receiver()
    {
        var envelope = new MessageEnvelope(new Ping(), "old");
        envelope.SetReceiver("new");
        Assert.Equal("new", envelope.Header.Receiver);
        Assert.Equal("server", envelope.Header.Sender);
    }

    [Fact]
    public void SetTimestamp_updates_the_timestamp()
    {
        var envelope = new MessageEnvelope(new Ping(), "r");
        var at = DateTime.UtcNow;
        envelope.SetTimestamp(at);
        Assert.Equal(at.Ticks, envelope.Header.Timestamp);
    }
}
