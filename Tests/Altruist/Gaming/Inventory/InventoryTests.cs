/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.Inventory;
using Altruist.Numerics;

namespace Tests.Gaming.Inventory;

// Test item types
public class TestSword : GameItem
{
    public int AttackPower { get; set; } = 10;
    public override string? EquipmentSlotType => "weapon_main";
}

public class TestPotion : GameItem
{
    public int HealAmount { get; set; } = 50;
}

public class TestChestArmor : GameItem
{
    public int Defense { get; set; } = 20;
    public override string? EquipmentSlotType => "chest";
}

public class TestSwordTemplate : ItemTemplate
{
    public TestSwordTemplate()
    {
        ItemId = 1001;
        Name = "Iron Sword";
        Category = "weapon";
        Size = new ByteVector2(1, 3);
        EquipmentSlotType = "weapon_main";
    }

    public override GameItem CreateInstance(short count = 1)
    {
        return new TestSword
        {
            TemplateId = ItemId,
            Category = Category,
            Count = count,
            Size = Size,
            AttackPower = 10
        };
    }
}

public class TestPotionTemplate : ItemTemplate
{
    public TestPotionTemplate()
    {
        ItemId = 2001;
        Name = "Health Potion";
        Category = "consumable";
        Stackable = true;
        MaxStack = 99;
    }

    public override GameItem CreateInstance(short count = 1)
    {
        return new TestPotion
        {
            TemplateId = ItemId,
            Category = Category,
            Count = count,
            Stackable = true,
            MaxStack = 99,
            HealAmount = 50
        };
    }
}

public class SlotStorageTests
{
    [Fact]
    public void PlaceItem_ShouldSucceed_InEmptySlot()
    {
        var storage = new SlotStorage("inventory", "player1", 10);
        var item = new TestSword { InstanceId = "sword1", TemplateId = 1001 };

        var result = storage.TryPlace(item, 0, 0, 1);

        Assert.Equal(ItemStatus.Success, result);
        var slot = storage.GetSlot(0, 0);
        Assert.NotNull(slot);
        Assert.Equal("sword1", slot.ItemInstanceId);
    }

    [Fact]
    public void PlaceAuto_ShouldFindFirstEmptySlot()
    {
        var storage = new SlotStorage("inventory", "player1", 10);
        var item1 = new TestSword { InstanceId = "s1", TemplateId = 1001 };
        var item2 = new TestSword { InstanceId = "s2", TemplateId = 1001 };

        storage.TryPlaceAuto(item1, 1);
        storage.TryPlaceAuto(item2, 1);

        Assert.Equal("s1", storage.GetSlot(0, 0)!.ItemInstanceId);
        Assert.Equal("s2", storage.GetSlot(1, 0)!.ItemInstanceId);
    }

    [Fact]
    public void Stacking_ShouldWork_ForStackableItems()
    {
        var storage = new SlotStorage("inventory", "player1", 10);
        var potion = new TestPotion { InstanceId = "p1", TemplateId = 2001, Stackable = true, MaxStack = 99 };

        storage.TryPlace(potion, 0, 0, 5);
        var result = storage.TryPlace(potion, 0, 0, 3);

        Assert.Equal(ItemStatus.Success, result);
        Assert.Equal(8, storage.GetSlot(0, 0)!.ItemCount);
    }

    [Fact]
    public void Remove_ShouldClearSlot()
    {
        var storage = new SlotStorage("inventory", "player1", 10);
        var item = new TestSword { InstanceId = "s1", TemplateId = 1001 };
        storage.TryPlace(item, 0, 0, 1);

        var result = storage.Remove(0, 0, 1);

        Assert.Equal(ItemStatus.Success, result);
        Assert.True(storage.GetSlot(0, 0)!.IsEmpty);
    }

    [Fact]
    public void NotEnoughSpace_WhenFull()
    {
        var storage = new SlotStorage("inventory", "player1", 1);
        var item1 = new TestSword { InstanceId = "s1", TemplateId = 1001 };
        var item2 = new TestSword { InstanceId = "s2", TemplateId = 1002 };

        storage.TryPlaceAuto(item1, 1);
        var result = storage.TryPlaceAuto(item2, 1);

        Assert.Equal(ItemStatus.NotEnoughSpace, result);
    }
}

public class GridStorageTests
{
    [Fact]
    public void PlaceMultiCellItem_ShouldOccupyAllCells()
    {
        var storage = new GridStorage("inventory", "player1", 10, 6);
        var sword = new TestSword
        {
            InstanceId = "s1", TemplateId = 1001,
            Size = new ByteVector2(1, 3) // 1 wide, 3 tall
        };

        var result = storage.TryPlace(sword, 0, 0, 1);

        Assert.Equal(ItemStatus.Success, result);
        Assert.Equal("s1", storage.GetSlot(0, 0)!.ItemInstanceId);
        Assert.Equal("s1", storage.GetSlot(0, 1)!.ItemInstanceId);
        Assert.Equal("s1", storage.GetSlot(0, 2)!.ItemInstanceId);
        Assert.True(storage.GetSlot(0, 1)!.IsLinked);
    }

    [Fact]
    public void CanFit_ShouldReject_WhenOverlapping()
    {
        var storage = new GridStorage("inventory", "player1", 10, 6);
        var item1 = new TestSword { InstanceId = "s1", Size = new ByteVector2(2, 2) };
        var item2 = new TestSword { InstanceId = "s2", Size = new ByteVector2(2, 2) };

        storage.TryPlace(item1, 0, 0, 1);
        var canFit = storage.CanFit(item2, 1, 1, 1);

        Assert.False(canFit);
    }

    [Fact]
    public void CanFit_ShouldReject_WhenOutOfBounds()
    {
        var storage = new GridStorage("inventory", "player1", 5, 5);
        var bigItem = new TestSword { InstanceId = "s1", Size = new ByteVector2(3, 3) };

        var canFit = storage.CanFit(bigItem, 4, 4, 1);

        Assert.False(canFit);
    }

    [Fact]
    public void Remove_ShouldClearAllLinkedCells()
    {
        var storage = new GridStorage("inventory", "player1", 10, 6);
        var sword = new TestSword { InstanceId = "s1", Size = new ByteVector2(2, 2) };

        storage.TryPlace(sword, 0, 0, 1);
        storage.Remove(0, 0, 1);

        Assert.True(storage.GetSlot(0, 0)!.IsEmpty);
        Assert.True(storage.GetSlot(1, 0)!.IsEmpty);
        Assert.True(storage.GetSlot(0, 1)!.IsEmpty);
        Assert.True(storage.GetSlot(1, 1)!.IsEmpty);
    }

    [Fact]
    public void PlaceAuto_ShouldFindFirstFitPosition()
    {
        var storage = new GridStorage("inventory", "player1", 10, 6);
        var item1 = new TestSword { InstanceId = "s1", Size = new ByteVector2(2, 2) };
        var item2 = new TestSword { InstanceId = "s2", Size = new ByteVector2(2, 2) };

        storage.TryPlaceAuto(item1, 1);
        var result = storage.TryPlaceAuto(item2, 1);

        Assert.Equal(ItemStatus.Success, result);
        Assert.Equal("s1", storage.GetSlot(0, 0)!.ItemInstanceId);
        Assert.Equal("s2", storage.GetSlot(2, 0)!.ItemInstanceId);
    }
}

public class EquipmentStorageTests
{
    private static EquipmentStorage CreateEquipment(string ownerId = "player1")
    {
        return new EquipmentStorage(ownerId, new[]
        {
            new EquipmentSlotDefinition { SlotName = "weapon_main", SlotIndex = 0, AcceptedCategories = ["weapon"] },
            new EquipmentSlotDefinition { SlotName = "chest", SlotIndex = 1, AcceptedCategories = ["armor"] },
            new EquipmentSlotDefinition { SlotName = "head", SlotIndex = 2, AcceptedCategories = ["helmet"] },
        });
    }

    [Fact]
    public void EquipItem_ShouldSucceed_InCompatibleSlot()
    {
        var equip = CreateEquipment();
        var sword = new TestSword { InstanceId = "s1", TemplateId = 1001, Category = "weapon" };

        var result = equip.TryPlace(sword, 0, 0, 1);

        Assert.Equal(ItemStatus.Success, result);
    }

    [Fact]
    public void EquipItem_ShouldFail_InIncompatibleSlot()
    {
        var equip = CreateEquipment();
        var sword = new TestSword { InstanceId = "s1", TemplateId = 1001, Category = "weapon" };

        var result = equip.TryPlace(sword, 1, 0, 1); // chest slot

        Assert.Equal(ItemStatus.IncompatibleSlot, result);
    }

    [Fact]
    public void EquipAuto_ShouldFindCompatibleSlot()
    {
        var equip = CreateEquipment();
        var sword = new TestSword { InstanceId = "s1", TemplateId = 1001, Category = "weapon" };

        var result = equip.TryPlaceAuto(sword, 1);

        Assert.Equal(ItemStatus.Success, result);
        Assert.Equal("s1", equip.GetSlot(0, 0)!.ItemInstanceId);
    }
}

public class InventoryServiceTests
{
    private InventoryService CreateService()
    {
        var templates = new ItemTemplateProvider();
        templates.Register(new TestSwordTemplate());
        templates.Register(new TestPotionTemplate());
        return new InventoryService(templates);
    }

    [Fact]
    public void CreateItem_ShouldReturnItemFromTemplate()
    {
        var service = CreateService();
        var item = service.CreateItem(1001);

        Assert.NotNull(item);
        Assert.IsType<TestSword>(item);
        Assert.Equal(1001, item.TemplateId);
    }

    [Fact]
    public void AddAndGetItem_ShouldWork()
    {
        var service = CreateService();
        service.CreateContainer("player1", new ContainerConfig
        {
            ContainerId = "inventory",
            ContainerType = ContainerType.Slot,
            SlotCount = 10
        });

        var item = service.CreateItem(1001);
        var result = service.AddItem("player1", "inventory", item);

        Assert.Equal(ItemStatus.Success, result);
        Assert.NotNull(service.GetItem(item.InstanceId));
    }

    [Fact]
    public async Task MoveItem_ShouldTransferBetweenContainers()
    {
        var service = CreateService();
        service.CreateContainer("player1", new ContainerConfig
        {
            ContainerId = "inventory", ContainerType = ContainerType.Slot, SlotCount = 10
        });
        service.CreateContainer("player1", new ContainerConfig
        {
            ContainerId = "bank", ContainerType = ContainerType.Slot, SlotCount = 20
        });

        var item = service.CreateItem(1001);
        service.AddItem("player1", "inventory", item);

        var from = new SlotKey(0, 0, "inventory", "player1");
        var to = SlotKey.Auto("bank", "player1");

        var result = await service.MoveItemAsync(from, to);

        Assert.Equal(ItemStatus.Success, result.Status);

        var invSlot = service.GetContainer("player1", "inventory")!.GetSlot(0, 0);
        Assert.True(invSlot!.IsEmpty);

        var bankSlot = service.GetContainer("player1", "bank")!.GetSlot(0, 0);
        Assert.False(bankSlot!.IsEmpty);
    }

    [Fact]
    public async Task EquipItem_ShouldMoveToEquipmentContainer()
    {
        var service = CreateService();
        service.CreateContainer("player1", new ContainerConfig
        {
            ContainerId = "inventory", ContainerType = ContainerType.Slot, SlotCount = 10
        });
        service.CreateContainer("player1", new ContainerConfig
        {
            ContainerId = "equipment",
            ContainerType = ContainerType.Equipment,
            EquipmentSlots = [
                new() { SlotName = "weapon_main", SlotIndex = 0, AcceptedCategories = ["weapon"] }
            ]
        });

        var sword = service.CreateItem(1001);
        service.AddItem("player1", "inventory", sword);

        var from = new SlotKey(0, 0, "inventory", "player1");
        var result = await service.EquipItemAsync("player1", from, "weapon_main");

        Assert.Equal(ItemStatus.Success, result.Status);
    }

    [Fact]
    public async Task DropAndPickup_ShouldWorkViaWorldContainer()
    {
        var service = CreateService();
        service.CreateContainer("player1", new ContainerConfig
        {
            ContainerId = "inventory", ContainerType = ContainerType.Slot, SlotCount = 10
        });

        var sword = service.CreateItem(1001);
        service.AddItem("player1", "inventory", sword);

        var from = new SlotKey(0, 0, "inventory", "player1");

        // Drop
        var dropResult = await service.DropItemAsync("player1", from);
        Assert.Equal(ItemStatus.Success, dropResult.Status);

        // Pickup
        var pickupResult = await service.PickupItemAsync("player1", sword.InstanceId);
        Assert.Equal(ItemStatus.Success, pickupResult.Status);
    }
}
