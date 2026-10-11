/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Autosave;

/// <summary>
/// A single WAL entry representing a dirty entity snapshot.
/// </summary>
/// <remarks>Stored as one JSON line per entry in the <see cref="WriteAheadLog{T}"/> file.</remarks>
public sealed class WalEntry
{
    /// <summary>Assembly-qualified name of the model type.</summary>
    public string TypeName { get; set; } = "";
    /// <summary>Entity storage id (recovery keeps only the latest entry per id).</summary>
    public string StorageId { get; set; } = "";
    /// <summary>Owner id the entity was marked dirty with.</summary>
    public string OwnerId { get; set; } = "";
    /// <summary>UTC time the snapshot was taken.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    /// <summary>Entity serialized with System.Text.Json (runtime type).</summary>
    public string Data { get; set; } = "";

    /// <summary>Parameterless constructor for JSON deserialization.</summary>
    public WalEntry() { }

    /// <summary>Creates an entry stamped with the current UTC time.</summary>
    /// <param name="typeName">Model type name.</param>
    /// <param name="storageId">Entity storage id.</param>
    /// <param name="ownerId">Owner id.</param>
    /// <param name="data">Serialized entity JSON.</param>
    public WalEntry(string typeName, string storageId, string ownerId, string data)
    {
        TypeName = typeName;
        StorageId = storageId;
        OwnerId = ownerId;
        Data = data;
        Timestamp = DateTime.UtcNow;
    }
}
