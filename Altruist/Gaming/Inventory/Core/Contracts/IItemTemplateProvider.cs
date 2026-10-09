/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Provides item template definitions. Templates can be registered programmatically
/// or loaded from JSON (deserialized directly into the user's template subclass).
/// </summary>
/// <remarks>
/// Default implementation <see cref="ItemTemplateProvider"/> is a singleton (<c>[Service]</c>) with thread-safe lookups.
/// Load templates once at startup; <see cref="IInventoryService.CreateItem"/> resolves templates from here.
/// </remarks>
/// <example>
/// <code>
/// templates.LoadFromJson&lt;MyItemTemplate&gt;("Data/items.json");
/// templates.Register(new MyItemTemplate { ItemId = 1001, Key = "health_potion", Stackable = true, MaxStack = 20 });
/// var item = inventory.CreateItem(templates.GetTemplateByKey("health_potion")!.ItemId, count: 3);
/// </code>
/// </example>
public interface IItemTemplateProvider
{
    /// <summary>Looks up a template by <see cref="ItemTemplate.ItemId"/>.</summary>
    /// <param name="templateId">Template id.</param>
    /// <returns>The template, or null.</returns>
    ItemTemplate? GetTemplate(long templateId);
    /// <summary>Looks up a template by <see cref="ItemTemplate.Key"/> (case-insensitive).</summary>
    /// <param name="key">Template key, e.g. <c>"iron_sword"</c>.</param>
    /// <returns>The template, or null.</returns>
    ItemTemplate? GetTemplateByKey(string key);
    /// <summary>All registered templates (unordered).</summary>
    IEnumerable<ItemTemplate> GetAllTemplates();
    /// <summary>Registers or replaces a template by id (and by key when the key is non-empty).</summary>
    /// <param name="template">Template to register.</param>
    void Register(ItemTemplate template);

    /// <summary>
    /// Load templates from a JSON file. Deserializes the JSON array directly
    /// into TTemplate (the user's ItemTemplate subclass) and registers each one.
    /// Property names are case-insensitive; comments and trailing commas are allowed.
    /// </summary>
    /// <typeparam name="TTemplate">Concrete template type to deserialize into.</typeparam>
    /// <param name="filePath">Path to a JSON file containing an array of templates.</param>
    void LoadFromJson<TTemplate>(string filePath) where TTemplate : ItemTemplate;

    /// <summary>
    /// Load templates from a JSON string (an array of templates) and registers each one.
    /// </summary>
    /// <typeparam name="TTemplate">Concrete template type to deserialize into.</typeparam>
    /// <param name="json">JSON array text.</param>
    /// <exception cref="InvalidOperationException">The JSON deserializes to null.</exception>
    void LoadFromJsonString<TTemplate>(string json) where TTemplate : ItemTemplate;
}
