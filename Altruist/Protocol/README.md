# Altruist.Protocol

Wire-protocol primitives for the Altruist framework. Shared by the server (`Altruist`) and client (`Altruist.Client`) packages so the on-the-wire shape has exactly one source of truth.

## What's in here

| Type | Purpose |
|---|---|
| `MessageEnvelope` | The fixarray(3) `[MessageCode, Header, Message]` shape every server→client packet ships in. |
| `PacketHeader` | Routing metadata (timestamp, sender, receiver). |
| `IPacketBase` | Interface every packet implements; declares `uint MessageCode { get; set; }`. |
| `IPacket` / `ITypedModel` | Marker interfaces above `IPacketBase`. |
| `PacketCodes` | Reserved framework message codes (`Sync = 3`, `Altruist = 4`, etc.). User packets start at 1000+. |
| `PacketHeaders` | Static helper exposing `PacketHeaders.Broadcast`. |
| `PacketBaseFormatter` | MessagePack formatter that handles polymorphic `IPacketBase` serialization (used by Core's typeless paths). |

## Why a separate package

Three implementations used to define the same wire shape:

1. The server's `Altruist` (Core) — for outbound `MessageEnvelope` encoding
2. The test harness's `Altruist.Testing/Clients/` — for integration-test clients
3. The Unity client's `PacketRouter.cs` — hand-rolled MessagePack skip parser

When any one drifted, packets vanished silently in production. Centralising the wire types here lets the server, the test clients, and the Unity client all bind against the same DLL.

## Target frameworks

`netstandard2.1` and `net9.0`. The first is the broadest portable contract (Unity Mono / IL2CPP, Godot, Mono, Blazor, .NET Framework 4.8 via shims); the second lets server-side consumers use it natively.

## AOT

The MessagePack-decorated structs (`MessageEnvelope`, `PacketHeader`) work with reflection-emit on Mono / .NET. For stripped runtimes (IL2CPP, NativeAOT, Blazor AOT), MessagePack source-generated formatters can be added — see the MessagePack documentation for `mpc` / `MessagePack.Generator` setup.
