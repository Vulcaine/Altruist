/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Altruist;
using Altruist.Persistence;
using Altruist.UORM;
using Moq;

public class CqlVaultTests
{
    private readonly Mock<IServiceProvider> _mockServiceProvider;

    private readonly Mock<ICqlDatabaseProvider> _mockDbProvider;
    private readonly Mock<IKeyspace> _mockKeyspace;
    private readonly CqlVault<TestVaultModel> _vault;
    private readonly CqlVault<TestHistoryVaultModel> _historyVault;

    public CqlVaultTests()
    {
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockDbProvider = new Mock<ICqlDatabaseProvider>();
        _mockKeyspace = new Mock<IKeyspace>();
        _vault = new CqlVault<TestVaultModel>(_mockDbProvider.Object, _mockKeyspace.Object, Document.From(typeof(TestVaultModel)), _mockServiceProvider.Object);
        _historyVault = new CqlVault<TestHistoryVaultModel>(_mockDbProvider.Object, _mockKeyspace.Object, Document.From(typeof(TestHistoryVaultModel)), _mockServiceProvider.Object);
    }

    [Fact]
    public async Task SaveAsync_InsertsRecord()
    {
        // Arrange
        var testModel = new TestVaultModel { Id = Guid.NewGuid(), Name = "Test" };
        _mockDbProvider.Setup(db => db.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object>>()))
                       .ReturnsAsync(1);

        // Act
        await _vault.SaveAsync(testModel);

        // Assert
        _mockDbProvider.Verify(db => db.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object>>()), Times.Once);
    }

    [Fact]
    public async Task SaveAsync_ThrowsException_WhenHistoryNotEnabled()
    {
        // Arrange
        var testModel = new TestVaultModel { Id = Guid.NewGuid(), Name = "Test" };

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _vault.SaveAsync(testModel, true));
    }

    [Fact]
    public async Task ToListAsync_ReturnsData()
    {
        // Arrange
        var mockData = new List<TestVaultModel>
        {
            new TestVaultModel { Id = Guid.NewGuid(), Name = "Item1" },
            new TestVaultModel { Id = Guid.NewGuid(), Name = "Item2" }
        };

        _mockDbProvider.Setup(db => db.QueryAsync<TestVaultModel>(It.IsAny<string>(), It.IsAny<List<object>>()))
                       .ReturnsAsync(mockData);

        // Act
        var result = await _vault.ToListAsync();

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_ReturnsNull_WhenNoData()
    {
        // Arrange
        _mockDbProvider.Setup(db => db.QueryAsync<TestVaultModel>(It.IsAny<string>(), It.IsAny<List<object>>()))
                       .ReturnsAsync(new List<TestVaultModel>());

        // Act
        var result = await _vault.FirstOrDefaultAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        // Arrange
        _mockDbProvider.Setup(db => db.ExecuteCountAsync(It.IsAny<string>(), It.IsAny<List<object>>()))
                       .ReturnsAsync(5);

        // Act
        var count = await _vault.CountAsync();

        // Assert
        Assert.Equal(5, count);
    }

    // [Fact]
    // public async Task UpdateAsync_UpdatesRecord()
    // {
    //     // Arrange
    //     _mockDbProvider.Setup(db => db.ExecuteAsync(It.IsAny<string>(), It.IsAny<object[]>()))
    //                    .ReturnsAsync(1);

    //     // Act
    //     var affectedRows = await _vault.UpdateAsync(v => new SetPropertyCalls<TestVaultModel> { Name = "Updated Name" });

    //     // Assert
    //     Assert.Equal(1, affectedRows);
    // }

    [Fact]
    public async Task DeleteAsync_ReturnsTrue_WhenSuccessful()
    {
        // Arrange
        _mockDbProvider.Setup(db => db.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object>>()))
                       .ReturnsAsync(1);

        // Act
        var result = await _vault.DeleteAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task SaveBatchAsync_InsertsMultipleRecords()
    {
        // Arrange
        var testModels = new List<TestVaultModel>
        {
            new TestVaultModel { Id = Guid.NewGuid(), Name = "Test1" },
            new TestVaultModel { Id = Guid.NewGuid(), Name = "Test2" }
        };

        _mockDbProvider.Setup(db => db.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object>>()))
                       .ReturnsAsync(2); // Simulating 2 rows affected

        // Act
        await _vault.SaveBatchAsync(testModels);

        // Assert
        _mockDbProvider.Verify(db => db.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object>>()), Times.Once);
    }

    [Fact]
    public void Where_AddsWhereClause()
    {
        // Arrange
        _vault.Where(v => v.Name == "Test");

        // Act
        var query = _vault.BuildSelectQuery();

        var document = Document.From(typeof(TestVaultModel));

        string expectedColumns = string.Join(", ", document.Columns.Select(c => $"{c.Value} AS {c.Key}"));
        string expectedTable = typeof(TestVaultModel).Name;
        string expectedWhereClause = "name = 'Test'";
        var expectedQuery = $"SELECT {expectedColumns} FROM {expectedTable} WHERE {expectedWhereClause}";

        // Assert
        Assert.Equal(expectedQuery, query);
    }


    [Fact]
    public void OrderBy_AddsOrderByClause()
    {
        // Arrange
        _vault.OrderBy(v => v.Name);

        // Act
        var query = _vault.BuildSelectQuery();

        string expectedColumns = "id AS Id, name AS Name, timestamp AS Timestamp, type AS Type, sysId AS SysId, groupId AS GroupId";
        string expectedTable = typeof(TestVaultModel).Name;
        string expectedOrderByClause = "ORDER BY Name";

        var expectedQuery = $"SELECT {expectedColumns} FROM {expectedTable} {expectedOrderByClause}";

        // Assert
        Assert.Equal(expectedQuery, query);
    }

    [Fact]
    public void Take_AddsLimitClause()
    {
        // Arrange
        _vault.Take(10);

        // Act
        var query = _vault.BuildSelectQuery();
        var document = Document.From(typeof(TestVaultModel));

        string expectedColumns = string.Join(", ", document.Columns.Select(c => $"{c.Value} AS {c.Key}"));
        string expectedTable = typeof(TestVaultModel).Name;
        string expectedLimitClause = "LIMIT 10";

        var expectedQuery = $"SELECT {expectedColumns} FROM {expectedTable} {expectedLimitClause}";

        // Assert
        Assert.Equal(expectedQuery, query);
    }

    [Fact]
    public void OrderByDescending_AddsOrderByDescClause()
    {
        // Arrange
        _vault.OrderByDescending(v => v.Name);

        // Act
        var query = _vault.BuildSelectQuery();

        var document = Document.From(typeof(TestVaultModel));

        string expectedColumns = string.Join(", ", document.Columns.Select(c => $"{c.Value} AS {c.Key}"));

        string expectedTable = typeof(TestVaultModel).Name;
        string expectedOrderByDescClause = "ORDER BY Name DESC";

        var expectedQuery = $"SELECT {expectedColumns} FROM {expectedTable} {expectedOrderByDescClause}";

        // Assert
        Assert.Equal(expectedQuery, query);
    }

    [Fact]
    public async Task SaveBatchAsync_HandlesHistory_WhenEnabled()
    {
        // Arrange
        var testModels = new List<TestHistoryVaultModel>
        {
            new TestHistoryVaultModel { Id = Guid.NewGuid(), Name = "Test1" },
            new TestHistoryVaultModel { Id = Guid.NewGuid(), Name = "Test2" }
        };

        var tableAttribute = new VaultAttribute("test_table", StoreHistory: true);
        var testModel = testModels[0];
        testModel.Timestamp = DateTime.UtcNow;

        _mockDbProvider.Setup(db => db.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object>>()))
                       .ReturnsAsync(2); // Simulating 2 rows affected

        // Act
        await _historyVault.SaveBatchAsync(testModels, saveHistory: true);

        // Assert
        _mockDbProvider.Verify(db => db.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object>>()), Times.Once);
    }

    [Fact]
    public void Where_AddsWhereClauseToUpdateQuery()
    {
        // Arrange
        _vault.Where(v => v.Name == "Test");

        // Act
        var query = _vault.BuildUpdateQuery();

        string expectedTable = typeof(TestVaultModel).Name;
        string expectedWhereClause = "WHERE name = 'Test'"; // Assuming the Name field is part of the WHERE clause

        var expectedQuery = $"UPDATE {expectedTable} SET  {expectedWhereClause}";

        // Assert
        Assert.Equal(expectedQuery, query);
    }

    [Fact]
    public void OrderBy_AddsOrderByClauseToUpdateQuery()
    {
        // Arrange
        _vault.OrderBy(v => v.Name);

        // Act
        var query = _vault.BuildUpdateQuery();

        string expectedTable = typeof(TestVaultModel).Name;
        var expectedQuery = $"UPDATE {expectedTable} SET ";

        // Assert
        Assert.Equal(expectedQuery, query);
    }

    [Fact]
    public void Take_DoesNotAddsLimitClauseToUpdateQuery()
    {
        // Arrange
        _vault.Take(10);

        // Act
        var query = _vault.BuildUpdateQuery();
        string expectedTable = typeof(TestVaultModel).Name;

        var expectedQuery = $"UPDATE {expectedTable} SET ";

        // Assert
        Assert.Equal(expectedQuery, query);
    }

    [Fact]
    public void OrderByDescending_AddsOrderByDescClauseToUpdateQuery()
    {
        // Arrange
        _vault.OrderByDescending(v => v.Name);

        // Act
        var query = _vault.BuildUpdateQuery();
        string expectedTable = typeof(TestVaultModel).Name;
        var expectedQuery = $"UPDATE {expectedTable} SET ";

        // Assert
        Assert.Equal(expectedQuery, query);
    }


    [Fact]
    public async Task SaveBatchAsync_ThrowsException_WhenHistoryNotEnabled()
    {
        // Arrange
        var testModels = new List<TestVaultModel>
        {
            new TestVaultModel { Id = Guid.NewGuid(), Name = "Test1" }
        };

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _vault.SaveBatchAsync(testModels, saveHistory: true));
    }
}

// Sample model for testing
public class TestVaultModel : IVaultModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string SysId { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = "";
}

[Vault("test", StoreHistory: true)]
public class TestHistoryVaultModel : IVaultModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string SysId { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = "";
}