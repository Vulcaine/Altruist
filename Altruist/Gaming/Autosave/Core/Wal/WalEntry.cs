/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Autosave;

/// <summary>
/// A single WAL entry representing a dirty entity snapshot.
/// </summary>
public sealed class WalEntry
{
    public string TypeName { get; set; } = "";
    public string StorageId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Data { get; set; } = "";

    public WalEntry() { }

    public WalEntry(string typeName, string storageId, string ownerId, string data)
    {
        TypeName = typeName;
        StorageId = storageId;
        OwnerId = ownerId;
        Data = data;
        Timestamp = DateTime.UtcNow;
    }
}
