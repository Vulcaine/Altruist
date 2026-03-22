/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.InMemory;
using Altruist.UORM;

namespace Tests.Persistence.Cache.InMemory;

public class InMemoryCacheProviderAdapterTests
{
    [Fact]
    public async Task Adapter_DelegatesToInMemoryCache()
    {
        var cache = new InMemoryCache();
        var adapter = new InMemoryCacheProviderAdapter(cache);

        await adapter.SaveAsync("key1", "value1");
        var result = await adapter.GetAsync<string>("key1");

        Assert.Equal("value1", result);
    }

    [Fact]
    public async Task Adapter_Remove_Works()
    {
        var cache = new InMemoryCache();
        var adapter = new InMemoryCacheProviderAdapter(cache);

        await adapter.SaveAsync("key1", "value1");
        var removed = await adapter.RemoveAsync<string>("key1");
        var result = await adapter.GetAsync<string>("key1");

        Assert.Equal("value1", removed);
        Assert.Null(result);
    }

    [Fact]
    public void Adapter_GetSnapshot_Works()
    {
        var cache = new InMemoryCache();
        var adapter = new InMemoryCacheProviderAdapter(cache);

        var snapshot = adapter.GetSnapshot();
        Assert.NotNull(snapshot);
    }
}

public class VaultAttributeTests
{
    [Fact]
    public void VaultAttribute_Default_DbInstance_IsEmpty()
    {
        var attr = new VaultAttribute("test_table");
        Assert.Equal("", attr.DbInstance);
        Assert.Equal("Postgres", attr.DbToken);
        Assert.Equal("altruist", attr.Keyspace);
    }

    [Fact]
    public void VaultAttribute_Custom_DbInstance()
    {
        var attr = new VaultAttribute("test_table", DbInstance: "replica");
        Assert.Equal("replica", attr.DbInstance);
    }

    [Fact]
    public void VaultAttribute_All_Params()
    {
        var attr = new VaultAttribute("analytics",
            StoreHistory: true,
            Keyspace: "reporting",
            DbToken: "Postgres",
            DbInstance: "readonly_replica");

        Assert.Equal("analytics", attr.Name);
        Assert.True(attr.StoreHistory);
        Assert.Equal("reporting", attr.Keyspace);
        Assert.Equal("readonly_replica", attr.DbInstance);
    }
}
