using MessagePack;
using MessagePack.Formatters;

namespace Altruist.Networking.Codec.MessagePack;

/// <summary>
/// MessagePack formatter that round-trips an <see cref="IPacketBase"/> reference
/// polymorphically. Wire shape: <c>fixarray(2) [AssemblyQualifiedName, [packet bytes]]</c>.
///
/// <para>Uses reflection (<see cref="Type.GetType(string)"/>) on the deserialize path,
/// so it does NOT work on stripped runtimes (IL2CPP / NativeAOT / Blazor AOT). For
/// AOT-safe paths, deserialize the inner packet as the concrete <c>T</c> at the call
/// site instead of as <c>IPacketBase</c>.</para>
///
/// <para>Applied automatically through the <c>[MessagePackFormatter]</c> attribute on
/// <see cref="IPacketBase"/> whenever a member is statically typed as that interface.
/// You never instantiate or register it yourself. <see cref="MessageEnvelope.Message"/> is
/// typed as <see cref="object"/> and therefore does not go through this formatter.</para>
/// </summary>
public class PacketBaseFormatter : IMessagePackFormatter<IPacketBase?>
{
    /// <summary>
    /// Writes <c>nil</c> for <c>null</c>; otherwise a 2-element array of the runtime type's
    /// assembly-qualified name followed by the packet serialized with the resolver's
    /// formatter for that runtime type.
    /// </summary>
    /// <param name="writer">MessagePack writer.</param>
    /// <param name="value">Packet to write, or <c>null</c>.</param>
    /// <param name="options">Serializer options whose resolver supplies the concrete formatter.</param>
    public void Serialize(ref MessagePackWriter writer, IPacketBase? value, MessagePackSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNil();
            return;
        }

        var type = value.GetType();
        writer.WriteArrayHeader(2);
        writer.Write(type.AssemblyQualifiedName);

        // The resolver's formatter is an IMessagePackFormatter<TConcrete>, which does not convert to
        // IMessagePackFormatter<IPacketBase?> (the interface is invariant): go through the runtime-typed API.
        MessagePackSerializer.Serialize(type, ref writer, value, options);
    }

    /// <summary>
    /// Reads the shape written by <see cref="Serialize"/>: resolves the type by name with
    /// <see cref="Type.GetType(string)"/> and deserializes the packet with that type's formatter. Only types
    /// implementing <see cref="IPacketBase"/> are accepted, so the wire cannot name an arbitrary type to instantiate.
    /// </summary>
    /// <param name="reader">MessagePack reader.</param>
    /// <param name="options">Serializer options whose resolver supplies the concrete formatter.</param>
    /// <returns>The packet, or <c>null</c> when the value is <c>nil</c>.</returns>
    /// <exception cref="InvalidOperationException">The array length is not 2, or the type name cannot be resolved to an <see cref="IPacketBase"/> type.</exception>
    public IPacketBase? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;

        var count = reader.ReadArrayHeader();
        if (count != 2)
            throw new InvalidOperationException($"Invalid array length: {count}");

        var typeName = reader.ReadString();
        var type = typeName is null ? null : Type.GetType(typeName);
        if (type == null)
            throw new InvalidOperationException($"Cannot find type: {typeName}");
        if (!typeof(IPacketBase).IsAssignableFrom(type))
            throw new InvalidOperationException($"Type {typeName} is not an {nameof(IPacketBase)}.");

        return (IPacketBase?)MessagePackSerializer.Deserialize(type, ref reader, options);
    }
}
