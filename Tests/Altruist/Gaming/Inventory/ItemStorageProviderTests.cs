using Moq;
using FluentAssertions;

namespace Altruist.Gaming;

class TestGameItem : GameItem
{
    public TestGameItem(SlotKey slotKey, int itemPropertySize = 4, byte width = 1, byte height = 1, string itemType = null!, bool isStackable = false, DateTime? expiryDate = null) : base(slotKey, itemPropertySize, width, height, itemType, isStackable, expiryDate)
    {
    }
}

public class ItemStorageProviderTests
{
    private readonly Mock<ICacheProvider> _cacheMock;
    private readonly ItemStorageProvider _storageProvider;
    private readonly SlotKey _testSlotKey;

    public ItemStorageProviderTests()
    {
        _cacheMock = new Mock<ICacheProvider>();
        _storageProvider = new ItemStorageProvider(
            new WorldStoragePrincipal("world"),
            "inventory", 5, 5, 5, _cacheMock.Object);
        _testSlotKey = new SlotKey(0, 0, "inventory", "inventory");
    }

    [Fact]
    public async Task FindItemAsync_ShouldReturnItem_WhenItemExistsInCache()
    {
        // Arrange
        var mockItem = new TestGameItem(
            new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", false
        );
        _cacheMock.Setup(c => c.GetAsync<GameItem>(mockItem.Id)).ReturnsAsync(mockItem);

        // Act
        var result = await _storageProvider.FindItemAsync<GameItem>(mockItem.Id);

        // Assert
        result.Should().BeEquivalentTo(mockItem);
    }

    [Fact]
    public async Task FindItemAsync_ShouldReturnNull_WhenItemDoesNotExistInCache()
    {
        // Arrange
        _cacheMock.Setup(c => c.GetAsync<GameItem>("item:inventory:1")).ReturnsAsync((GameItem?)null);

        // Act
        var result = await _storageProvider.FindItemAsync<GameItem>("1");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetItemAsync_ShouldReturnFalse_WhenItemNotFound()
    {
        // Arrange
        _cacheMock.Setup(c => c.GetAsync<GameItem>("item:inventory:1")).ReturnsAsync((GameItem?)null);

        // Act
        var result = await _storageProvider.SetItemAsync("1", 5, _testSlotKey);

        // Assert
        result.Should().Be(SetItemStatus.ItemNotFound);
    }

    [Fact]
    public async Task SetItemAsync_ShouldReturnFalse_WhenNoSpaceAtLocation()
    {
        // Arrange
        var mockItem = new TestGameItem(
            new SlotKey(0, 0, "inventory", "inventory"), 4, 1, 1, "type", true);
        _cacheMock.Setup(c => c.GetAsync<GameItem>(mockItem.Id)).ReturnsAsync(mockItem);
        _storageProvider.AddItem(mockItem, 1, "inventory");
        // Act
        var result = await _storageProvider.SetItemAsync(mockItem.Id, 6, _testSlotKey);

        // Assert
        result.Should().Be(SetItemStatus.NotEnoughSpace);
    }

    [Fact]
    public async Task SetItemAsync_ShouldPlaceItem_WhenSpaceAvailable()
    {
        // Arrange
        var mockItem = new TestGameItem(
            new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", true);
        _cacheMock.Setup(c => c.GetAsync<GameItem>(mockItem.Id)).ReturnsAsync(mockItem);

        // Act
        var result = await _storageProvider.SetItemAsync(mockItem.Id, 5, _testSlotKey);

        // Assert
        result.Should().Be(SetItemStatus.Success);
    }

    [Fact]
    public void AddItem_ShouldReturnFalse_WhenNoSpaceAvailable()
    {
        // Arrange
        var item = new TestGameItem(new SlotKey(0, 0, "inventory", "inventory"), 4, 5, 6, "type", true);

        // Act
        var result = _storageProvider.AddItem(item, 10, "inventory");

        // Assert
        result.Should().Be(AddItemStatus.NotEnoughSpace);
    }

    [Fact]
    public void AddItem_ShouldAddItem_WhenSpaceAvailable()
    {
        // Arrange
        var item = new TestGameItem(new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", false);

        // Act
        var result = _storageProvider.AddItem(item, 1, "inventory");

        // Assert
        result.Should().Be(AddItemStatus.Success);
    }

    [Fact]
    public void AddItem_ShouldAddItem_WhenSpaceAvailable_AndItemStackable()
    {
        // Arrange
        var item = new TestGameItem(new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", true);

        // Act
        var result = _storageProvider.AddItem(item, 5, "inventory");

        // Assert
        result.Should().Be(AddItemStatus.Success);
    }

    [Fact]
    public void AddItem_ShouldNotAddItem_WhenSpaceAvailable_AndItemNotStackable()
    {
        // Arrange
        var item = new TestGameItem(new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type");

        // Act
        var result = _storageProvider.AddItem(item, 5, "inventory");

        // Assert
        result.Should().Be(AddItemStatus.NonStackable);
    }

    [Fact]
    public void AddItem_ShouldAddItem_WhenSpaceBarelyAvailable()
    {
        // Arrange
        var item = new TestGameItem(new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", true);

        // Act
        var result = _storageProvider.AddItem(item, 5, "inventory");

        // Assert
        result.Should().Be(AddItemStatus.Success);
    }

    [Fact]
    public void RemoveItem_ShouldReturnEmpty_WhenItemNotFound()
    {
        // Arrange
        var key = new SlotKey(0, 0, "inventory", "inventory");

        // Act
        var result = _storageProvider.RemoveItem(key);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveItem_ShouldReturnStorageSlot_WhenItemRemovedSuccessfully()
    {
        // Arrange
        var testSlot = new SlotKey(0, 0, "inventory", "inventory");
        var testItem = new TestGameItem(testSlot, 4, 2, 2, "type", true);

        _storageProvider.AddItem(testItem, 5, "inventory");
        var result = _storageProvider.RemoveItem(testSlot, 5);
        var itemInStorage = await _storageProvider.FindItemAsync<GameItem>(testItem.Id);
        var slot = _storageProvider.FindSlot(testSlot);

        // Assert
        result.Should().NotBeNull();
        result.First().ItemCount.Should().Be(5);
        result.First().ItemInstanceId.Should().Be(testItem.Id);
        itemInStorage.Should().BeNull();
        slot.Should().NotBeNull();
        slot.ItemCount.Should().Be(0);
        slot.ItemInstanceId.Should().Be("");
    }

    [Fact]
    public async Task SwapSlotsAsync_ShouldSwapItems_WhenBothSlotsAreOccupied()
    {
        // Arrange
        var slotA = new SlotKey(0, 0, "inventory", "inventory");
        var slotB = new SlotKey(3, 3, "inventory", "inventory");

        var itemA = new TestGameItem(slotA, 4, 1, 1, "item-type-a", true);
        var itemB = new TestGameItem(slotB, 4, 1, 1, "item-type-b", true);

        _storageProvider.AddItem(itemA, 4, "inventory");
        _storageProvider.AddItem(itemB, 2, "inventory");

        // Mock
        _cacheMock.Setup(c => c.GetAsync<GameItem>(itemA.Id)).ReturnsAsync(itemA);
        _cacheMock.Setup(c => c.GetAsync<GameItem>(itemB.Id)).ReturnsAsync(itemB);

        // Act
        var result = await _storageProvider.SwapSlotsAsync(slotA, slotB);

        // Assert
        result.Should().Be(SwapSlotStatus.Success);

        var newSlotA = _storageProvider.FindSlot(slotA);
        var newSlotB = _storageProvider.FindSlot(slotB);

        newSlotA.Should().NotBeNull();
        newSlotA.ItemInstanceId.Should().Be(itemB.Id);
        newSlotA.ItemCount.Should().Be(2);

        newSlotB.Should().NotBeNull();
        newSlotB.ItemInstanceId.Should().Be(itemA.Id);
        newSlotB.ItemCount.Should().Be(4);
    }


    [Fact]
    public async Task MoveItemAsync_ShouldReturnFalse_WhenItemNotFound()
    {
        // Arrange
        _cacheMock.Setup(c => c.GetAsync<GameItem>("item:inventory:1")).ReturnsAsync((GameItem?)null);
        var fromSlotKey = new SlotKey(0, 0, "inventory", "inventory");
        var toSlotKey = new SlotKey(1, 0, "inventory", "inventory");

        // Act
        var result = await _storageProvider.MoveItemAsync("1", fromSlotKey, toSlotKey, 1);

        // Assert
        result.Should().Be(MoveItemStatus.ItemNotFound);
    }

    [Fact]
    public async Task MoveItemAsync_ShouldReturnFalse_WhenNoSpaceAtTarget()
    {
        // Arrange
        var fromSlotKey = new SlotKey(0, 0, "inventory", "inventory");
        var toSlotKey = new SlotKey(1, 0, "inventory", "inventory");

        var mockItem = new TestGameItem(
            fromSlotKey, 4, 2, 2, "type", false);
        _cacheMock.Setup(c => c.GetAsync<GameItem>(mockItem.Id)).ReturnsAsync(mockItem);

        _storageProvider.AddItem(mockItem, 1, fromSlotKey.Id);

        // Act
        var result = await _storageProvider.MoveItemAsync(mockItem.Id, fromSlotKey, toSlotKey, 1);

        // Assert
        result.Should().Be(MoveItemStatus.NotEnoughSpace);
    }

    [Fact]
    public async Task MoveItemAsync_ShouldReturnTrue_WhenMovedSuccessfully()
    {
        // Arrange
        var mockItem = new TestGameItem(
            new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", false);
        _cacheMock.Setup(c => c.GetAsync<GameItem>(mockItem.Id)).ReturnsAsync(mockItem);
        _storageProvider.AddItem(mockItem, 1, "inventory");
        var fromSlotKey = new SlotKey(0, 0, "inventory", "inventory");
        var toSlotKey = new SlotKey(2, 0, "inventory", "inventory");
        // Act
        var result = await _storageProvider.MoveItemAsync(mockItem.Id, fromSlotKey, toSlotKey, 1);

        // Assert
        result.Should().Be(MoveItemStatus.Success);
    }

    [Fact]
    public async Task MoveItemAsync_ShouldReturnFalse_WhenEmptySlotMovedAttempt()
    {
        // Arrange
        var mockItem = new TestGameItem(
            new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", false);
        _cacheMock.Setup(c => c.GetAsync<GameItem>(mockItem.Id)).ReturnsAsync(mockItem);
        var fromSlotKey = new SlotKey(0, 0, "inventory", "inventory");
        var toSlotKey = new SlotKey(1, 0, "inventory", "inventory");
        // Act
        var result = await _storageProvider.MoveItemAsync(mockItem.Id, fromSlotKey, toSlotKey, 1);

        // Assert
        result.Should().Be(MoveItemStatus.CannotMove);
    }
}
