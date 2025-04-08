using Moq;

namespace Altruist.Gaming
{
    // public class InventoryServiceTests
    // {
    //     private readonly Mock<ICacheProvider> _cacheMock;
    //     private readonly InventoryService _inventoryService;

    //     public InventoryServiceTests()
    //     {
    //         _cacheMock = new Mock<ICacheProvider>();
    //         _inventoryService = new InventoryService(_cacheMock.Object);
    //     }

    //     // Helper method to create a mock storage object
    //     private InventoryStorage CreateMockStorage(string storageId)
    //     {
    //         return new InventoryStorage(storageId)
    //         {
    //             SlotMap = new Dictionary<string, StorageSlot>()
    //         };
    //     }

    //     // Test GetStorageAsync method
    //     [Fact]
    //     public async Task GetStorageAsync_ShouldReturnStorage_WhenStorageExists()
    //     {
    //         // Arrange
    //         var storageId = "storage1";
    //         var mockStorage = CreateMockStorage(storageId);
    //         _cacheMock.Setup(c => c.GetAsync<InventoryStorage>($"storage:{storageId}"))
    //                   .ReturnsAsync(mockStorage);

    //         // Act
    //         var result = await _inventoryService.GetStorageAsync(storageId);

    //         // Assert
    //         Assert.NotNull(result);
    //         Assert.Equal(storageId, result.StorageId);
    //     }

    //     [Fact]
    //     public async Task GetStorageAsync_ShouldCreateAndReturnNewStorage_WhenStorageDoesNotExist()
    //     {
    //         // Arrange
    //         var storageId = "storage2";
    //         _cacheMock.Setup(c => c.GetAsync<InventoryStorage>($"storage:{storageId}"))
    //                   .ReturnsAsync((InventoryStorage)null);

    //         // Act
    //         var result = await _inventoryService.GetStorageAsync(storageId);

    //         // Assert
    //         Assert.NotNull(result);
    //         Assert.Equal(storageId, result.StorageId);
    //         _cacheMock.Verify(c => c.SaveAsync($"storage:{storageId}", It.IsAny<InventoryStorage>()), Times.Once);
    //     }

    //     // Test SetItemAsync method
    //     [Fact]
    //     public async Task SetItemAsync_ShouldAddItemToStorage()
    //     {
    //         // Arrange
    //         var storageId = "storage1";
    //         var itemId = 123L;
    //         var itemCount = (short)5;
    //         var x = (short)0;
    //         var y = (short)0;
    //         var slotId = "inventory";

    //         var mockStorage = CreateMockStorage(storageId);
    //         _cacheMock.Setup(c => c.GetAsync<InventoryStorage>($"storage:{storageId}"))
    //                   .ReturnsAsync(mockStorage);

    //         // Act
    //         await _inventoryService.SetItemAsync(storageId, itemId, itemCount, x, y, slotId);

    //         // Assert
    //         Assert.Contains(mockStorage.SlotMap, kvp => kvp.Value.ItemId == itemId && kvp.Value.ItemCount == itemCount);
    //         _cacheMock.Verify(c => c.SaveAsync($"storage:{storageId}", mockStorage), Times.Once);
    //     }

    //     // Test MoveItemAsync method
    //     [Fact]
    //     public async Task MoveItemAsync_ShouldMoveItemBetweenStorages()
    //     {
    //         // Arrange
    //         var storageId = "storage1";
    //         var targetStorageId = "storage2";
    //         var itemId = 123L;
    //         var x = (short)0;
    //         var y = (short)0;
    //         var fromSlotId = "inventory";
    //         var toSlotId = "inventory";

    //         var sourceStorage = CreateMockStorage(storageId);
    //         var targetStorage = CreateMockStorage(targetStorageId);

    //         var sourceKey = $"slot:{storageId}:{fromSlotId}:{x}:{y}";
    //         var targetKey = $"slot:{targetStorageId}:{toSlotId}:{x}:{y}";

    //         sourceStorage.SlotMap[sourceKey] = new StorageSlot
    //         {
    //             ItemId = itemId,
    //             ItemCount = 5,
    //             X = x,
    //             Y = y
    //         };

    //         _cacheMock.Setup(c => c.GetAsync<InventoryStorage>($"storage:{storageId}")).ReturnsAsync(sourceStorage);
    //         _cacheMock.Setup(c => c.GetAsync<InventoryStorage>($"storage:{targetStorageId}")).ReturnsAsync(targetStorage);

    //         // Act
    //         await _inventoryService.MoveItemAsync(itemId, storageId, targetStorageId, x, y, fromSlotId, toSlotId);

    //         // Assert
    //         Assert.Contains(targetStorage.SlotMap, kvp => kvp.Value.ItemId == itemId);
    //         Assert.DoesNotContain(sourceStorage.SlotMap, kvp => kvp.Key == sourceKey);
    //         _cacheMock.Verify(c => c.SaveAsync($"storage:{storageId}", sourceStorage), Times.Once);
    //         _cacheMock.Verify(c => c.SaveAsync($"storage:{targetStorageId}", targetStorage), Times.Once);
    //     }

    //     // Test RemoveItemAsync method
    //     [Fact]
    //     public async Task RemoveItemAsync_ShouldRemoveItemFromStorage()
    //     {
    //         // Arrange
    //         var storageId = "storage1";
    //         var x = (short)0;
    //         var y = (short)0;
    //         var slotId = "inventory";

    //         var mockStorage = CreateMockStorage(storageId);
    //         var key = $"slot:{storageId}:{slotId}:{x}:{y}";
    //         mockStorage.SlotMap[key] = new StorageSlot { ItemId = 123L, ItemCount = 5 };

    //         _cacheMock.Setup(c => c.GetAsync<InventoryStorage>($"storage:{storageId}")).ReturnsAsync(mockStorage);

    //         // Act
    //         await _inventoryService.RemoveItemAsync(storageId, x, y, slotId);

    //         // Assert
    //         Assert.Empty(mockStorage.SlotMap);
    //         _cacheMock.Verify(c => c.SaveAsync($"storage:{storageId}", mockStorage), Times.Once);
    //     }

    //     // Test UseItemAsync method
    //     [Fact]
    //     public async Task UseItemAsync_ShouldRemoveItemWhenUsed()
    //     {
    //         // Arrange
    //         var storageId = "storage1";
    //         var itemId = 123L;

    //         var mockStorage = CreateMockStorage(storageId);
    //         var key = $"slot:{storageId}:inventory:0:0";
    //         mockStorage.SlotMap[key] = new StorageSlot { ItemId = itemId, ItemCount = 5 };

    //         _cacheMock.Setup(c => c.GetAsync<InventoryStorage>($"storage:{storageId}")).ReturnsAsync(mockStorage);

    //         // Act
    //         await _inventoryService.UseItemAsync(storageId, itemId);

    //         // Assert
    //         Assert.Empty(mockStorage.SlotMap);
    //         _cacheMock.Verify(c => c.SaveAsync($"storage:{storageId}", mockStorage), Times.Once);
    //     }

    //     // Test SortStorageAsync method
    //     [Fact]
    //     public async Task SortStorageAsync_ShouldSortItemsInStorage()
    //     {
    //         // Arrange
    //         var storageId = "storage1";
    //         var mockStorage = CreateMockStorage(storageId);

    //         mockStorage.SlotMap["slot1"] = new StorageSlot { ItemId = 2, ItemCount = 10 };
    //         mockStorage.SlotMap["slot2"] = new StorageSlot { ItemId = 1, ItemCount = 5 };

    //         _cacheMock.Setup(c => c.GetAsync<InventoryStorage>($"storage:{storageId}")).ReturnsAsync(mockStorage);

    //         // Act
    //         await _inventoryService.SortStorageAsync(storageId);

    //         // Assert
    //         var sortedKeys = mockStorage.SlotMap.Keys.ToList();
    //         Assert.Equal("0:0", sortedKeys[0]);
    //         Assert.Equal("0:1", sortedKeys[1]);
    //         _cacheMock.Verify(c => c.SaveAsync($"storage:{storageId}", mockStorage), Times.Once);
    //     }
    // }
}
