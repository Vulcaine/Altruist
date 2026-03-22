/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Text.Json;

namespace Altruist.Gaming.Inventory;

[Service(typeof(IItemTemplateProvider))]
public class ItemTemplateProvider : IItemTemplateProvider
{
    private readonly ConcurrentDictionary<long, ItemTemplate> _byId = new();
    private readonly ConcurrentDictionary<string, ItemTemplate> _byKey = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ItemTemplate? GetTemplate(long templateId)
        => _byId.TryGetValue(templateId, out var t) ? t : null;

    public ItemTemplate? GetTemplateByKey(string key)
        => _byKey.TryGetValue(key, out var t) ? t : null;

    public IEnumerable<ItemTemplate> GetAllTemplates() => _byId.Values;

    public void Register(ItemTemplate template)
    {
        _byId[template.ItemId] = template;
        if (!string.IsNullOrEmpty(template.Key))
            _byKey[template.Key] = template;
    }

    public void LoadFromJson<TTemplate>(string filePath) where TTemplate : ItemTemplate
    {
        var json = File.ReadAllText(filePath);
        LoadFromJsonString<TTemplate>(json);
    }

    public void LoadFromJsonString<TTemplate>(string json) where TTemplate : ItemTemplate
    {
        var templates = JsonSerializer.Deserialize<List<TTemplate>>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize item templates from JSON.");

        foreach (var template in templates)
            Register(template);
    }
}
