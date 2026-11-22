/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.InMemory;

using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Framework;

/// <summary>
/// RoomPacket.ConnectionIds is a plain HashSet shared by every connection in a room (all sockets
/// start in the waiting room). Before AbstractConnectionStore serialized membership changes, a
/// connect/disconnect storm on many threads lost entries or corrupted the set. These tests are
/// a stress test: without the lock they fail (or throw) on most runs, not deterministically.
/// </summary>
public sealed class ConnectionStoreConcurrencyTests
{
    private const int Connections = 4000;

    private static async Task<InMemoryConnectionStore> NewStoreAsync(params string[] rooms)
    {
        var store = new InMemoryConnectionStore(new InMemoryCache(), NullLoggerFactory.Instance);
        await store.CreateRoomAsync(StoreConstants.WaitingRoomId);
        foreach (var room in rooms)
            await store.CreateRoomAsync(room);
        return store;
    }

    private static Task RunParallel(int count, Func<int, Task> body) =>
        Parallel.ForEachAsync(Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(8, Environment.ProcessorCount * 2) },
            async (i, _) => await body(i));

    [Fact]
    public async Task Concurrent_connects_into_one_room_keep_every_connection()
    {
        var store = await NewStoreAsync();

        await RunParallel(Connections, i =>
            store.AddConnectionAsync($"c{i}", new AltruistConnection(), StoreConstants.WaitingRoomId));

        var room = await store.GetRoomAsync(StoreConstants.WaitingRoomId);
        Assert.NotNull(room);
        Assert.Equal(Connections, room!.ConnectionIds.Count);
        Assert.All(Enumerable.Range(0, Connections), i => Assert.Contains($"c{i}", room.ConnectionIds));
    }

    [Fact]
    public async Task Concurrent_connects_and_disconnects_leave_exactly_the_survivors()
    {
        var store = await NewStoreAsync();
        await RunParallel(Connections, i =>
            store.AddConnectionAsync($"c{i}", new AltruistConnection(), StoreConstants.WaitingRoomId));

        // Disconnect the even ones while new connections keep arriving.
        await RunParallel(Connections * 2, i => i < Connections
            ? (i % 2 == 0 ? store.RemoveConnectionAsync($"c{i}") : Task.CompletedTask)
            : store.AddConnectionAsync($"n{i}", new AltruistConnection(), StoreConstants.WaitingRoomId));

        var room = (await store.GetRoomAsync(StoreConstants.WaitingRoomId))!;
        var expected = Enumerable.Range(0, Connections).Where(i => i % 2 == 1).Select(i => $"c{i}")
            .Concat(Enumerable.Range(Connections, Connections).Select(i => $"n{i}"))
            .ToHashSet();
        Assert.True(expected.SetEquals(room.ConnectionIds),
            $"room has {room.ConnectionIds.Count} ids, expected {expected.Count}");
    }

    [Fact]
    public async Task Concurrent_room_switches_never_lose_or_duplicate_a_connection()
    {
        var store = await NewStoreAsync("room-a", "room-b");
        await RunParallel(Connections, i =>
            store.AddConnectionAsync($"c{i}", new AltruistConnection(), StoreConstants.WaitingRoomId));

        await RunParallel(Connections, i => store.JoinRoomAsync($"c{i}", i % 2 == 0 ? "room-a" : "room-b"));

        var a = await store.GetRoomAsync("room-a");
        var b = await store.GetRoomAsync("room-b");
        Assert.Equal(Connections / 2, a!.ConnectionIds.Count);
        Assert.Equal(Connections / 2, b!.ConnectionIds.Count);
        Assert.Empty(a.ConnectionIds.Intersect(b.ConnectionIds));

        // Reading a room's members while it changes must not throw ("collection was modified").
        var reads = Task.Run(async () =>
        {
            for (var k = 0; k < 200; k++)
                await store.GetConnectionsInRoomAsync("room-a");
        });
        await RunParallel(Connections / 2, i => store.RemoveConnectionAsync($"c{i * 2}"));
        await reads;

        // Everyone left the waiting room via JoinRoomAsync: it must still exist (it used to be
        // deleted there, after which new connections could not join it any more).
        var waiting = await store.GetRoomAsync(StoreConstants.WaitingRoomId);
        Assert.NotNull(waiting);
        Assert.True(await store.AddConnectionAsync("late-comer", new AltruistConnection(), StoreConstants.WaitingRoomId));
    }
}
