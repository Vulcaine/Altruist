using System.Text;
using System.Text.Json;
using Altruist;
using Altruist.Client;
using Altruist.Http;
using Microsoft.Extensions.Configuration;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Tests.Altruist.Client;

/// <summary>
/// Golden wire vectors shared with the TypeScript client SDK (<c>Clients/TypeScript/net/tests/wire.test.ts</c>
/// reads the same <c>Resources/WireVectors/wire-vectors.json</c>): MessagePack-CSharp must produce exactly
/// these bytes for the values, server envelopes and client frames, so the TypeScript encoder / decoder and
/// <c>peekMessageCode</c> / <c>extractMessage</c> are byte-compatible with the server; and the server's
/// <see cref="TokenBucketRateLimiter"/> / <see cref="SlidingWindowCounter"/> must give the verdicts the
/// TypeScript <c>TokenBucket</c> / <c>SlidingWindowLimiter</c> twins give (<c>util.test.ts</c>).
/// </summary>
public sealed class WireVectorTests
{
    /// <summary>The packet used by the envelope and frame vectors.</summary>
    [MessagePackObject]
    public sealed class WirePacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 4242;
        [Key(1)] public string Name { get; set; } = "";
        [Key(2)] public int Score { get; set; }
    }

    private static readonly MessagePackSerializerOptions ServerOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                StandardResolverAllowPrivate.Instance,
                TypelessContractlessStandardResolver.Instance));

    private static JsonElement Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "WireVectors", "wire-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("altruist.wire-vectors/1", doc.RootElement.GetProperty("format").GetString());
        return doc.RootElement.Clone();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static byte[] Serialize(string type, JsonElement value) => type switch
    {
        "int" => MessagePackSerializer.Serialize(value.GetInt32()),
        "uint" => MessagePackSerializer.Serialize(value.GetUInt32()),
        "long" => MessagePackSerializer.Serialize(value.GetInt64()),
        "float" => MessagePackSerializer.Serialize(value.GetSingle()),
        "double" => MessagePackSerializer.Serialize(value.GetDouble()),
        "bool" => MessagePackSerializer.Serialize(value.GetBoolean()),
        "nil" => MessagePackSerializer.Serialize<string?>(null),
        "string" => MessagePackSerializer.Serialize(value.GetString()),
        "int[]" => MessagePackSerializer.Serialize(value.EnumerateArray().Select(e => e.GetInt32()).ToArray()),
        _ => throw new InvalidOperationException($"Unknown vector type {type}"),
    };

    [Fact]
    public void Values_SerializeToTheSharedBytes()
    {
        foreach (var v in Vectors().GetProperty("values").EnumerateArray())
        {
            var name = v.GetProperty("name").GetString();
            var bytes = Serialize(v.GetProperty("type").GetString()!, v.GetProperty("value"));
            Assert.True(v.GetProperty("hex").GetString() == Hex(bytes), $"{name}: expected {v.GetProperty("hex").GetString()}, MessagePack-CSharp wrote {Hex(bytes)}");
        }
    }

    [Fact]
    public void Envelopes_SerializeToTheSharedBytes_AndParseWithEnvelopeShape()
    {
        foreach (var e in Vectors().GetProperty("envelopes").EnumerateArray())
        {
            var h = e.GetProperty("header");
            var p = e.GetProperty("packet");
            var packet = new WirePacket
            {
                MessageCode = p.GetProperty("messageCode").GetUInt32(),
                Name = p.GetProperty("name").GetString()!,
                Score = p.GetProperty("score").GetInt32(),
            };
            var header = new PacketHeader
            {
                Timestamp = long.Parse(h.GetProperty("timestamp").GetString()!),
                Receiver = h.GetProperty("receiver").ValueKind == JsonValueKind.Null ? null : h.GetProperty("receiver").GetString(),
                Sender = h.GetProperty("sender").GetString()!,
            };
            var bytes = MessagePackSerializer.Serialize(new MessageEnvelope(header, packet), ServerOptions);
            Assert.Equal(e.GetProperty("hex").GetString(), Hex(bytes));

            var golden = Convert.FromHexString(e.GetProperty("hex").GetString()!);
            Assert.Equal(e.GetProperty("code").GetUInt32(), MessageEnvelopeShape.PeekMessageCode(golden));
            var inner = new MessagePackClientCodec().Deserialize<WirePacket>(MessageEnvelopeShape.ExtractMessage(golden));
            Assert.NotNull(inner);
            Assert.Equal(packet.Name, inner!.Name);
            Assert.Equal(packet.Score, inner.Score);
        }
    }

    [Fact]
    public void ClientFrames_AreEventPrefixedPacketsTheServerReads()
    {
        foreach (var f in Vectors().GetProperty("clientFrames").EnumerateArray())
        {
            var frame = Convert.FromHexString(f.GetProperty("hex").GetString()!);
            var ev = f.GetProperty("event").GetString()!;
            var p = f.GetProperty("packet");

            // The server's binary framing (ConnectionManager): [u8 len 1..127][event UTF-8][payload].
            int len = frame[0];
            Assert.InRange(len, 1, 127);
            Assert.Equal(ev, Encoding.UTF8.GetString(frame, 1, len));
            var payload = frame.AsSpan(1 + len).ToArray();

            var packet = MessagePackSerializer.Deserialize<WirePacket>(payload, ServerOptions);
            Assert.Equal(p.GetProperty("messageCode").GetUInt32(), packet.MessageCode);
            Assert.Equal(p.GetProperty("name").GetString(), packet.Name);
            Assert.Equal(p.GetProperty("score").GetInt32(), packet.Score);

            // And MessagePack-CSharp writes the same payload bytes for that packet.
            Assert.Equal(Hex(payload), Hex(MessagePackSerializer.Serialize(packet, ServerOptions)));
        }
    }

    [Fact]
    public void TokenBuckets_GiveTheSharedVerdicts()
    {
        foreach (var b in Vectors().GetProperty("tokenBuckets").EnumerateArray())
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["altruist:server:transport:rate-limit:strikes-to-disconnect"] = "0",
                ["altruist:server:transport:rate-limit:buckets:b:capacity"] = b.GetProperty("capacity").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["altruist:server:transport:rate-limit:buckets:b:per-second"] = b.GetProperty("perSecond").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["altruist:server:transport:rate-limit:buckets:b:gates:0"] = "g",
            }).Build();
            var now = 0.0;
            var limiter = new TokenBucketRateLimiter(RateLimitOptions.FromConfiguration(cfg), () => now);
            var i = 0;
            foreach (var hit in b.GetProperty("hits").EnumerateArray())
            {
                now = hit.GetProperty("at").GetDouble();
                var expected = hit.GetProperty("allow").GetBoolean() ? RateVerdict.Allow : RateVerdict.Drop;
                Assert.True(expected == limiter.Check("c", "g", 1), $"{b.GetProperty("name").GetString()} hit {i} at {now}");
                i++;
            }
        }
    }

    [Fact]
    public void SlidingWindows_GiveTheSharedDecisions()
    {
        foreach (var w in Vectors().GetProperty("slidingWindows").EnumerateArray())
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var now = start;
            var counter = new SlidingWindowCounter(() => now);
            var window = TimeSpan.FromSeconds(w.GetProperty("windowSeconds").GetDouble());
            var permit = w.GetProperty("permit").GetInt32();
            foreach (var hit in w.GetProperty("hits").EnumerateArray())
            {
                now = start + TimeSpan.FromSeconds(hit.GetProperty("at").GetDouble());
                var d = counter.Hit("k", permit, window);
                Assert.Equal(hit.GetProperty("allowed").GetBoolean(), d.Allowed);
                Assert.Equal(hit.GetProperty("retryAfterSeconds").GetInt32(), d.RetryAfterSeconds);
            }
        }
    }
}
