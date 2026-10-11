using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Client;

/// <summary>
/// Default codec. Mirrors the server's <c>Altruist.Codec.MessagePack.MessagePackMessageEncoder</c>
/// resolver bag (<see cref="StandardResolverAllowPrivate"/> + <see cref="TypelessContractlessStandardResolver"/>)
/// so wire output is byte-identical between client and server when the same
/// packet types are registered on both sides.
///
/// <para>Two ctors:</para>
/// <list type="bullet">
///   <item>Parameterless — uses the default resolver bag. Marked
///   <see cref="ActivatorUtilitiesConstructorAttribute"/> so Altruist DI picks
///   it (its ctor selector otherwise prefers the widest public ctor and would
///   fail trying to resolve <see cref="MessagePackSerializerOptions"/>).
///   Fine on Mono/.NET; on IL2CPP / NativeAOT the typeless resolver may fail
///   for game-specific <c>object</c>-typed fields without registered
///   formatters — the explicit-options ctor below is the AOT-safe path.</item>
///   <item>Takes <see cref="MessagePackSerializerOptions"/> — consumer plugs in
///   their own composite resolver including AOT-generated formatters for game
///   packet types. Used by tests and by AOT consumers via direct construction.</item>
/// </list>
///
/// <para>Provider name <c>"messagepack"</c> (alias <c>"msgpack"</c> in
/// <see cref="ClientCodecResolver"/>); this is the default when a transport's codec block is
/// omitted. Use <see cref="JsonClientCodec"/> only for debugging / browser-style text
/// frames.</para>
/// </summary>
/// <example>
/// <code>
/// // AOT (IL2CPP / NativeAOT): supply generated formatters explicitly
/// var options = MessagePackSerializerOptions.Standard.WithResolver(
///     CompositeResolver.Create(GeneratedResolver.Instance, StandardResolver.Instance));
/// var codec = new MessagePackClientCodec(options);
/// </code>
/// </example>
[Service(typeof(IClientCodec))]
public sealed class MessagePackClientCodec : IClientCodec
{
    private static readonly MessagePackSerializerOptions DefaultOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                StandardResolver.Instance,
                StandardResolverAllowPrivate.Instance,
                TypelessContractlessStandardResolver.Instance));

    private readonly MessagePackSerializerOptions _options;

    /// <summary>Creates the codec with the default resolver bag (the DI constructor).</summary>
    [ActivatorUtilitiesConstructor]
    public MessagePackClientCodec() : this(DefaultOptions) { }
    /// <summary>Creates the codec with caller-supplied options (the AOT-safe path).</summary>
    /// <param name="options">Serializer options, typically with a composite resolver containing generated formatters.</param>
    public MessagePackClientCodec(MessagePackSerializerOptions options) => _options = options;

    /// <summary>Always <c>"messagepack"</c>.</summary>
    public string Provider => "messagepack";

    /// <inheritdoc/>
    public byte[] Serialize<T>(T value) =>
        value is null ? Array.Empty<byte>() : MessagePackSerializer.Serialize(value, _options);

    /// <inheritdoc/>
    public T? Deserialize<T>(byte[] data) =>
        data is null || data.Length == 0 ? default : MessagePackSerializer.Deserialize<T>(data, _options);

    /// <inheritdoc/>
    /// <remarks>Copies the span into a new array before deserializing.</remarks>
    public T? Deserialize<T>(ReadOnlySpan<byte> data) =>
        data.IsEmpty ? default : MessagePackSerializer.Deserialize<T>(new ReadOnlyMemory<byte>(data.ToArray()), _options);
}
