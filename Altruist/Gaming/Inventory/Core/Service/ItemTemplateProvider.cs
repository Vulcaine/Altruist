/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist.Gaming.Inventory;

[Service(typeof(IItemTemplateProvider))]
public class ItemTemplateProvider : IItemTemplateProvider
{
    private readonly ConcurrentDictionary<long, ItemTemplate> _templates = new();

    public ItemTemplate? GetTemplate(long templateId)
        => _templates.TryGetValue(templateId, out var t) ? t : null;

    public IEnumerable<ItemTemplate> GetAllTemplates() => _templates.Values;

    public void Register(ItemTemplate template)
    {
        _templates[template.ItemId] = template;
    }
}
