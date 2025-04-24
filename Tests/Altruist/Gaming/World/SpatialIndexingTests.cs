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

using FluentAssertions;

namespace Altruist.Gaming;

public class SpatialGridIndexTests
{
    [Fact]
    public void Add_ShouldStoreObjectInGridAndTypeMap()
    {
        var index = new SpatialGridIndex(10);
        var obj = new ObjectMetadata
        {
            InstanceId = "obj1",
            RoomId = "room1",
            Position = new IntVector2(15, 25),
            Type = new WorldObjectTypeKey("NPC")
        };

        index.Add(obj.Type, obj);

        var cellKey = "1:2"; // 15/10 = 1, 25/10 = 2
        index.Grid[cellKey].Should().Contain("obj1");
        index.TypeMap["NPC"].Should().Contain("obj1");
        index.InstanceMap.Should().ContainKey("obj1");
    }

    [Fact]
    public void Remove_ShouldDeleteObjectFromAllMaps()
    {
        var index = new SpatialGridIndex(10);
        var type = new WorldObjectTypeKey("Player");
        var obj = new ObjectMetadata
        {
            InstanceId = "p1",
            RoomId = "r1",
            Position = new IntVector2(10, 10),
            Type = type
        };

        index.Add(type, obj);
        var removed = index.Remove(type, "p1");

        removed.Should().Be(obj);
        index.InstanceMap.Should().NotContainKey("p1");
        index.TypeMap["Player"].Should().NotContain("p1");
        index.Grid["1:1"].Should().NotContain("p1");
    }

    [Fact]
    public void Query_ShouldReturnObjectsInRadiusAndSameRoom()
    {
        var index = new SpatialGridIndex(10);

        var inside = new ObjectMetadata
        {
            InstanceId = "a",
            Position = new IntVector2(15, 15),
            RoomId = "room1",
            Type = new WorldObjectTypeKey("Item")
        };

        var outside = new ObjectMetadata
        {
            InstanceId = "b",
            Position = new IntVector2(100, 100),
            RoomId = "room1",
            Type = new WorldObjectTypeKey("Item")
        };

        var wrongRoom = new ObjectMetadata
        {
            InstanceId = "c",
            Position = new IntVector2(16, 16),
            RoomId = "room2",
            Type = new WorldObjectTypeKey("Item")
        };

        index.Add(inside.Type, inside);
        index.Add(outside.Type, outside);
        index.Add(wrongRoom.Type, wrongRoom);

        var results = index.Query(inside.Type, 15, 15, 5, "room1");

        results.Should().ContainSingle()
            .Which.InstanceId.Should().Be("a");
    }

    [Fact]
    public void GetByType_ShouldReturnCorrectInstances()
    {
        var index = new SpatialGridIndex(10);

        var player = new ObjectMetadata { InstanceId = "p", Position = new IntVector2(1, 1), RoomId = "room", Type = new WorldObjectTypeKey("Player") };
        var enemy = new ObjectMetadata { InstanceId = "e", Position = new IntVector2(2, 2), RoomId = "room", Type = new WorldObjectTypeKey("Enemy") };

        index.Add(player.Type, player);
        index.Add(enemy.Type, enemy);

        var result = index.GetByType(player.Type);

        result.Should().ContainKey("p");
        result.Should().NotContainKey("e");
    }

    [Fact]
    public void GetAllByType_ShouldReturnAllInstancesOfType()
    {
        var index = new SpatialGridIndex(10);
        var type = new WorldObjectTypeKey("Loot");

        var obj1 = new ObjectMetadata { InstanceId = "l1", Position = new IntVector2(0, 0), RoomId = "room", Type = type };
        var obj2 = new ObjectMetadata { InstanceId = "l2", Position = new IntVector2(5, 5), RoomId = "room", Type = type };

        index.Add(type, obj1);
        index.Add(type, obj2);

        var result = index.GetAllByType(type);

        result.Should().HaveCount(2);
        result.Select(x => x.InstanceId).Should().Contain(["l1", "l2"]);
    }
}
