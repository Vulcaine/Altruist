/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Provides item template definitions. Templates can be registered programmatically
/// or loaded from JSON (deserialized directly into the user's template subclass).
/// </summary>
public interface IItemTemplateProvider
{
    ItemTemplate? GetTemplate(long templateId);
    ItemTemplate? GetTemplateByKey(string key);
    IEnumerable<ItemTemplate> GetAllTemplates();
    void Register(ItemTemplate template);

    /// <summary>
    /// Load templates from a JSON file. Deserializes the JSON array directly
    /// into TTemplate (the user's ItemTemplate subclass).
    /// </summary>
    void LoadFromJson<TTemplate>(string filePath) where TTemplate : ItemTemplate;

    /// <summary>
    /// Load templates from a JSON string directly.
    /// </summary>
    void LoadFromJsonString<TTemplate>(string json) where TTemplate : ItemTemplate;
}
