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
/// </summary>
public class PacketBaseFormatter : IMessagePackFormatter<IPacketBase?>
{
    public void Serialize(ref MessagePackWriter writer, IPacketBase? value, MessagePackSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNil();
            return;
        }

        var type = value.GetType();
        var typeName = type.AssemblyQualifiedName;

        writer.WriteArrayHeader(2);
        writer.Write(typeName);

        var formatter = options.Resolver.GetFormatterDynamic(type);
        var specificFormatter = (IMessagePackFormatter<IPacketBase?>)formatter!;

        specificFormatter.Serialize(ref writer, value, options);
    }

    public IPacketBase? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;

        var count = reader.ReadArrayHeader();
        if (count != 2)
            throw new InvalidOperationException($"Invalid array length: {count}");

        var typeName = reader.ReadString();
        var type = Type.GetType(typeName!);
        if (type == null)
            throw new InvalidOperationException($"Cannot find type: {typeName}");

        var formatter = options.Resolver.GetFormatterDynamic(type);
        var specificFormatter = (IMessagePackFormatter<IPacketBase?>)formatter!;

        var result = specificFormatter.Deserialize(ref reader, options);
        return result;
    }
}
