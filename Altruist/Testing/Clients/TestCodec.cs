/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text;
using System.Text.Json;

using MessagePack;
using MessagePack.Resolvers;

using Microsoft.Extensions.Configuration;

namespace Altruist.Testing;

/// <summary>
/// Encode/decode an Altruist packet payload. Picks JSON or MessagePack based on
/// the transport's <c>codec:provider</c> setting in <c>config.yml</c> — same
/// resolution path Altruist itself uses, so the test client speaks whatever the
/// server is configured for without a per-transport hardcode.
/// </summary>
public interface ITestCodec
{
    /// <summary>The codec name: <c>json</c> or <c>messagepack</c>.</summary>
    string Provider { get; }
    /// <summary>Encodes a payload; null encodes to an empty array.</summary>
    byte[] Serialize(object? payload);
    /// <summary>Decodes a payload; an empty array decodes to <c>default</c>.</summary>
    T? Deserialize<T>(byte[] data);
    /// <summary>Decodes a payload as an untyped array (positional packets), or null when it is not one.</summary>
    object[]? DeserializeArray(byte[] data);
}

/// <summary>Picks the <see cref="ITestCodec"/> matching the server's codec configuration (the test clients call it; use it directly to encode payloads by hand).</summary>
public static class TestCodecResolver
{
    /// <summary>
    /// Resolve the codec for a given transport. Looks up
    /// <c>altruist:server:transport:&lt;transport&gt;:codec:provider</c> first,
    /// then falls back to the global <c>altruist:server:transport:codec:provider</c>,
    /// matching how Altruist's own transports resolve codec config. Default: MessagePack.
    /// </summary>
    /// <param name="cfg">The test configuration.</param>
    /// <param name="transportName">The transport key (<c>tcp</c>, <c>udp</c>, <c>websocket</c>).</param>
    /// <returns>The JSON or MessagePack codec.</returns>
    /// <exception cref="InvalidOperationException">The configured provider is neither json nor messagepack.</exception>
    public static ITestCodec Resolve(IConfiguration cfg, string transportName)
    {
        var perTransport = cfg[$"altruist:server:transport:{transportName}:codec:provider"];
        var global = cfg["altruist:server:transport:codec:provider"];
        var provider = (perTransport ?? global ?? "messagepack").Trim().ToLowerInvariant();

        return provider switch
        {
            "json" => JsonTestCodec.Instance,
            "messagepack" or "msgpack" => MessagePackTestCodec.Instance,
            _ => throw new InvalidOperationException(
                $"Unknown codec provider '{provider}' for transport '{transportName}'. " +
                "Expected 'json' or 'messagepack' in config.yml."),
        };
    }
}

internal sealed class MessagePackTestCodec : ITestCodec
{
    public static readonly MessagePackTestCodec Instance = new();

    /// <summary>Resolver that mirrors what the integration suite has historically
    /// used — covers private setters, contractless types, and typeless polymorphism
    /// some Altruist packets rely on.</summary>
    public static readonly MessagePackSerializerOptions Opts =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                StandardResolver.Instance,
                StandardResolverAllowPrivate.Instance,
                TypelessContractlessStandardResolver.Instance));

    public string Provider => "messagepack";

    public byte[] Serialize(object? payload) =>
        payload is null ? Array.Empty<byte>() : MessagePackSerializer.Serialize(payload, Opts);

    public T? Deserialize<T>(byte[] data) =>
        data.Length == 0 ? default : MessagePackSerializer.Deserialize<T>(data, Opts);

    public object[]? DeserializeArray(byte[] data)
    {
        try { return MessagePackSerializer.Deserialize<object[]>(data, Opts); }
        catch { return null; }
    }
}

internal sealed class JsonTestCodec : ITestCodec
{
    public static readonly JsonTestCodec Instance = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    public string Provider => "json";

    public byte[] Serialize(object? payload) =>
        payload is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Opts));

    public T? Deserialize<T>(byte[] data) =>
        data.Length == 0 ? default : JsonSerializer.Deserialize<T>(data, Opts);

    public object[]? DeserializeArray(byte[] data)
    {
        try { return JsonSerializer.Deserialize<object[]>(data, Opts); }
        catch { return null; }
    }
}
