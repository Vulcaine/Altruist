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
        result.Should().BeFalse();
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
        result.Should().BeFalse();
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
        result.Should().BeTrue();
    }

    [Fact]
    public void AddItem_ShouldReturnFalse_WhenNoSpaceAvailable()
    {
        // Arrange
        var item = new TestGameItem(new SlotKey(0, 0, "inventory", "inventory"), 4, 5, 6, "type", false);

        // Act
        var result = _storageProvider.AddItem(item, 10, "inventory");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void AddItem_ShouldAddItem_WhenSpaceAvailable()
    {
        // Arrange
        var item = new TestGameItem(new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", false);

        // Act
        var result = _storageProvider.AddItem(item, 1, "inventory");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void AddItem_ShouldAddItem_WhenSpaceAvailable_AndItemStackable()
    {
        // Arrange
        var item = new TestGameItem(new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", true);

        // Act
        var result = _storageProvider.AddItem(item, 5, "inventory");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void AddItem_ShouldAddItem_WhenSpaceBarelyAvailable()
    {
        // Arrange
        var item = new TestGameItem(new SlotKey(0, 0, "inventory", "inventory"), 4, 2, 2, "type", false);

        // Act
        var result = _storageProvider.AddItem(item, 5, "inventory");

        // Assert
        result.Should().BeTrue();
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
        var testItem = new TestGameItem(testSlot, 4, 2, 2, "type", false);

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
    public async Task MoveItemAsync_ShouldReturnFalse_WhenItemNotFound()
    {
        // Arrange
        _cacheMock.Setup(c => c.GetAsync<GameItem>("item:inventory:1")).ReturnsAsync((GameItem?)null);
        var fromSlotKey = new SlotKey(0, 0, "inventory", "inventory");
        var toSlotKey = new SlotKey(1, 0, "inventory", "inventory");

        // Act
        var result = await _storageProvider.MoveItemAsync("1", fromSlotKey, toSlotKey, 1);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task MoveItemAsync_ShouldReturnFalse_WhenNoSpaceAtTarget()
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
        result.Should().BeFalse();
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
        result.Should().BeTrue();
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
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SortStorageAsync_ShouldSortItems()
    {
        // Arrange
        var mockItem1 = new TestGameItem(
            new SlotKey(0, 0, "inventory", "inventory"), 4, 1, 1, "A", false);
        var mockItem2 = new TestGameItem(
            new SlotKey(0, 0, "inventory", "inventory"), 4, 1, 1, "B", false);

        _cacheMock.Setup(c => c.GetAsync<GameItem>("item:inventory:1")).ReturnsAsync(mockItem1);
        _cacheMock.Setup(c => c.GetAsync<GameItem>("item:inventory:2")).ReturnsAsync(mockItem2);

        // Act
        await _storageProvider.SortStorageAsync();

        // Assert
        var sortedItems = await _storageProvider.GetAllItemsAsync<GameItem>();
        // sortedItems.Should().BeInAscendingOrder();
    }
}
