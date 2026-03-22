/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Provides item template definitions. Users register templates at startup
/// or load them from config/files/database.
/// </summary>
public interface IItemTemplateProvider
{
    ItemTemplate? GetTemplate(long templateId);
    IEnumerable<ItemTemplate> GetAllTemplates();
    void Register(ItemTemplate template);
}
