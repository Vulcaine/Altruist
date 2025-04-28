using MessagePack;
using MessagePack.Formatters;

namespace Altruist.Networking.Codec.MessagePack;

public class PacketBaseFormatter : IMessagePackFormatter<IPacketBase>
{
    public void Serialize(ref MessagePackWriter writer, IPacketBase value, MessagePackSerializerOptions options)
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
        var specificFormatter = (IMessagePackFormatter<IPacketBase>)formatter!;

        // Serialize the value using the specific formatter
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
        var specificFormatter = (IMessagePackFormatter<IPacketBase>)formatter!;

        var result = specificFormatter.Deserialize(ref reader, options);
        return result;
    }
}
