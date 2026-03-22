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
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class CodecProviderAttribute : Attribute
{
    public string Name { get; }

    public CodecProviderAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}
