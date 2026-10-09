/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist;

/// <summary>
/// Names a codec implementation so it can be selected via config.
/// Example: [CodecProvider("json")] or [CodecProvider("messagepack")]
/// Users can create custom codecs with any name: [CodecProvider("protobuf")]
/// </summary>
/// <remarks>
/// Put it on an <see cref="ICodec"/> implementation. <see cref="ICodecResolver"/> discovers every such type in the
/// loaded assemblies (whether or not it is DI-registered) and matches <see cref="Name"/> case-insensitively against
/// <c>altruist:server:transport:codec:provider</c> and <c>altruist:server:transport:&lt;mode&gt;:codec:provider</c>.
/// Names must be unique; on a clash a DI-registered codec wins over one found only by reflection.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class CodecProviderAttribute : Attribute
{
    /// <summary>Provider name used in config (compared case-insensitively).</summary>
    public string Name { get; }

    /// <summary>Names the codec.</summary>
    /// <param name="name">Provider name referenced from config, e.g. <c>"json"</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
    public CodecProviderAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}
