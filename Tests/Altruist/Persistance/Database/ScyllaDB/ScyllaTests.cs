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

using Moq;
using Cassandra;
using Cassandra.Mapping;
using Altruist.ScyllaDB;
using Altruist;

public class ScyllaDbProviderTests
{
    private readonly Mock<ISession> _mockSession;
    private readonly Mock<IMapper> _mockMapper;
    private readonly ScyllaDBToken _mockToken = ScyllaDBToken.Instance;
    private readonly ScyllaDbProvider _scyllaDbProvider;

    public ScyllaDbProviderTests()
    {
        _mockSession = new Mock<ISession>();
        _mockMapper = new Mock<IMapper>();

        // Initialize the provider with mocked dependencies
        _scyllaDbProvider = new ScyllaDbProvider(_mockSession.Object, _mockMapper.Object, _mockToken);

        // Use reflection to set private readonly fields
        typeof(ScyllaDbProvider).GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(_scyllaDbProvider, _mockSession.Object);
        typeof(ScyllaDbProvider).GetField("_mapper", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(_scyllaDbProvider, _mockMapper.Object);
    }

    [Fact]
    public async Task QueryAsync_ReturnsExpectedResults()
    {
        // Arrange
        var expectedResults = new List<TestModel> { new TestModel { Id = 1, Name = "Test" } };
        _mockMapper.Setup(m => m.FetchAsync<TestModel>("SELECT * FROM table", It.IsAny<object[]>()))
                   .ReturnsAsync(expectedResults);

        // Act
        var results = await _scyllaDbProvider.QueryAsync<TestModel>("SELECT * FROM table");

        // Assert
        Assert.Equal(expectedResults.Count, results.Count());
        Assert.Equal(expectedResults[0].Name, results.First().Name);
    }

    [Fact]
    public async Task QuerySingleAsync_ReturnsSingleResult()
    {
        // Arrange
        var expectedResult = new TestModel { Id = 1, Name = "SingleTest" };
        _mockMapper.Setup(m => m.FetchAsync<TestModel>("SELECT * FROM table WHERE id = ?", It.IsAny<object[]>()))
                   .ReturnsAsync(new List<TestModel> { expectedResult });

        // Act
        var result = await _scyllaDbProvider.QuerySingleAsync<TestModel>("SELECT * FROM table WHERE id = ?", new List<object> { 1 });

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedResult.Name, result.Name);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsAffectedRows()
    {
        // Arrange
        var mockRowSet = new Mock<RowSet>();
        var mockRow = new Mock<Row>();

        // Simulate rows in the RowSet by mocking the Rows property
        var rows = new List<Row> { mockRow.Object };
        mockRowSet.Setup(r => r.GetEnumerator()).Returns(rows.GetEnumerator());

        // Mock ExecutionInfo to prevent null reference issues
        var mockExecutionInfo = new Mock<ExecutionInfo>();
        mockRowSet.Setup(r => r.Info).Returns(mockExecutionInfo.Object);

        var mockPreparedStatement = new Mock<PreparedStatement>();
        var mockBoundStatement = new Mock<BoundStatement>();

        _mockSession.Setup(s => s.Prepare(It.IsAny<string>())).Returns(mockPreparedStatement.Object);
        _mockSession.Setup(s => s.ExecuteAsync(It.IsAny<BoundStatement>())).ReturnsAsync(mockRowSet.Object);

        // Act
        var affectedRows = await _scyllaDbProvider.ExecuteAsync("UPDATE table SET name = ? WHERE id = ?", new List<object> { "Updated", 1 });

        // Assert
        Assert.Equal(1, affectedRows);
    }



    [Fact]
    public async Task UpdateAsync_CallsMapperUpdate()
    {
        // Arrange
        var entity = new TestModel { Id = 1, Name = "Updated" };
        _mockMapper.Setup(m => m.UpdateAsync(entity, It.IsAny<CqlQueryOptions>())).Returns(Task.CompletedTask);

        // Act
        var result = await _scyllaDbProvider.UpdateAsync(entity);

        // Assert
        Assert.Equal(1, result);
        _mockMapper.Verify(m => m.UpdateAsync(entity, It.IsAny<CqlQueryOptions>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_CallsMapperDelete()
    {
        // Arrange
        var entity = new TestModel { Id = 1, Name = "ToDelete" };
        _mockMapper.Setup(m => m.DeleteAsync(entity, It.IsAny<CqlQueryOptions>())).Returns(Task.CompletedTask);

        // Act
        var result = await _scyllaDbProvider.DeleteAsync(entity);

        // Assert
        Assert.Equal(1, result);
        _mockMapper.Verify(m => m.DeleteAsync(entity, It.IsAny<CqlQueryOptions>()), Times.Once);
    }

    [Fact]
    public async Task CreateKeySpaceAsync_CallsCreateKeyspaceIfNotExists()
    {
        // Act
        await _scyllaDbProvider.CreateKeySpaceAsync("test_keyspace");

        // Assert
        _mockSession.Verify(s => s.CreateKeyspaceIfNotExists("test_keyspace", It.IsAny<Dictionary<string, string>>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task ChangeKeyspaceAsync_SendsUseCommand()
    {
        // Act
        await _scyllaDbProvider.ChangeKeyspaceAsync("new_keyspace");

        // Assert
        _mockSession.Verify(s => s.ExecuteAsync(It.Is<SimpleStatement>(stmt => stmt.QueryString == "USE new_keyspace;")), Times.Once);
    }
}

/// <summary>
/// Sample test model implementing IVaultModel
/// </summary>
public class TestModel : IVaultModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string SysId { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = "";
}
