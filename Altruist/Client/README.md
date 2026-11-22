# Altruist.Client

Drop-in client for any Altruist server. Speaks the same wire protocol the server speaks — TCP / UDP / WebSocket framing, MessagePack or JSON codec, gate-name dispatch, fixarray(3) envelope. Use it from Unity, Godot, Blazor, console games, headless integration test harnesses — anywhere `netstandard2.1` runs.

## Install

```xml
<PackageReference Include="Altruist.Client" Version="0.9.5-beta" />
```

For Unity, build the DLL and drop it (and `Altruist.Protocol.dll`, `MessagePack.dll`) into `Assets/Plugins/`.

## Wire shape (read-only reference)

| Step | Bytes |
|---|---|
| Server→client handshake (TCP only) | `[4 LE length][UTF-8 clientId]` |
| Framing both directions (TCP) | `[4 LE length][payload]` |
| Client→server payload | `[1 byte gateLen][gate UTF-8][codec(packet)]` |
| Server→client payload | codec-encoded `MessageEnvelope` = fixarray(3) `[MessageCode (uint), Header, Message]` |

The package owns this shape. Don't reimplement it in the consumer.

## Three transports

| Class | Use |
|---|---|
| `AltruistTcpClient` | Login, gameplay, anything ordered. Background read thread → `IncomingQueue`. |
| `AltruistUdpClient` | Movement, voice, anything droppable. |
| `AltruistWebSocketClient` | Browser clients, JSON debug. |

All three share the same lifecycle surface: `OnConnected`, `OnDisconnected`, `OnError(Exception)`, `IsConnected`, `ClientId`. All async APIs accept a `CancellationToken`.

## Wiring

```csharp
using Altruist;
using Altruist.Client;

var endpoint = new EndpointConfig("127.0.0.1", 13000, "messagepack");
var codec = MessagePackClientCodec.Instance;

var router = new PacketRouter(codec);
router.Register<DamageInfo>(PC.Damage, OnDamage);
router.Register<SCharacterAdd>(PC.CharacterAdd, OnCharacterAdd);

await using var tcp = new AltruistTcpClient(endpoint, codec);
await tcp.ConnectAsync();                       // handshake reads ClientId

tcp.StartReadLoop();                            // pumps frames into IncomingQueue

// In your main loop / Update tick:
while (tcp.IncomingQueue.TryDequeue(out var payload))
    router.Dispatch(payload);

await tcp.SendAsync("login", new CLogin { Username = "alice" });

void OnDamage(DamageInfo d) { /* … */ }
void OnCharacterAdd(SCharacterAdd c) { /* … */ }
```

## AOT support

The receive path NEVER deserializes the outer envelope as a struct — `MessageEnvelopeShape` byte-skips the fixarray(3), peeks element [0] (MessageCode), slices element [2] (the inner packet bytes). Then `MessagePackSerializer.Deserialize<T>(slicedBytes, options)` is invoked with the concrete `T` registered for that MessageCode. Both operations are AOT-safe given a registered formatter for `T`.

Game-specific packet formatters are the consumer's responsibility:

```csharp
var resolver = MessagePack.Resolvers.CompositeResolver.Create(
    GeneratedResolver.Instance,                      // your mpc-generated formatters
    StandardResolver.Instance);
var options = MessagePackSerializerOptions.Standard.WithResolver(resolver);
var codec = new MessagePackClientCodec(options);
```

The default `MessagePackClientCodec.Instance` uses reflection-friendly resolvers (works on Mono / .NET, fails on stripped IL2CPP / NativeAOT for game-specific packets without registered formatters).

## Gotchas

- **Resolver must match the server.** If the server adds a custom `IMessagePackFormatter` for some type, the client codec needs the same formatter or wire-incompatible bytes follow.
- **Two registrations of the same MC silently overwrite.** Last `Register` wins. Mirrors server `[Service]` last-wins.
- **Concurrent `SendAsync` is safe.** `AltruistTcpClient` locks the underlying stream so multi-thread send won't interleave bytes (a real production bug we caught in tests).
- **`StartReadLoop` is mutually exclusive with `DrainAsync`.** The first runs a background thread that enqueues into `IncomingQueue`; the second is for tests that want to await batches. Pick one per consumer.
