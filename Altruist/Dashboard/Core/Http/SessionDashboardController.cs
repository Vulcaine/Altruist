/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Altruist.Dashboard
{
    /// <summary>
    /// Dashboard controller exposing session / room information
    /// for the live transport (WebSocket / connections), route <c>/dashboard/v1/sessions</c>.
    /// Can list connections (with remote IP addresses) and forcibly disconnect clients or delete rooms.
    /// </summary>
    /// <remarks>
    /// Only mapped when <c>altruist:dashboard:enabled</c> is <c>true</c>; every request must pass the dashboard
    /// protection (<see cref="DashboardAccessOptions"/>: token, policy, or Development loopback). Requires
    /// <see cref="IConnectionManager"/> and <see cref="ISocketManager"/> in DI.
    /// </remarks>
    [ApiController]
    [Route("/dashboard/v1/sessions")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class SessionDashboardController : ControllerBase
    {
        private readonly IConnectionManager _connectionManager;
        private readonly ISocketManager _socketManager;
        private readonly ILogger<SessionDashboardController> _logger;

        /// <summary>Creates the controller.</summary>
        /// <param name="connectionManager">Performs engine-aware disconnects.</param>
        /// <param name="socketManager">Source of rooms and connections.</param>
        /// <param name="logger">Logs dashboard-initiated disconnects.</param>
        public SessionDashboardController(
            IConnectionManager connectionManager,
            ISocketManager socketManager,
            ILogger<SessionDashboardController> logger)
        {
            _connectionManager = connectionManager;
            _socketManager = socketManager;
            _logger = logger;
        }

        /// <summary>
        /// <c>GET /dashboard/v1/sessions</c>: 200 with every room (ordered by id, case-insensitive) and its connection ids
        /// as <see cref="RoomSessionDto"/> items.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<RoomSessionDto>>> GetSessions()
        {
            var roomsDict = await _socketManager.GetAllRoomsAsync();
            var result = new List<RoomSessionDto>();

            foreach (var kvp in roomsDict)
            {
                var roomId = kvp.Key;
                var connectionsDict = await _socketManager.GetConnectionsInRoomAsync(roomId);

                var connections = connectionsDict
                    .Select(c => new ConnectionDto
                    {
                        ConnectionId = c.Key
                    })
                    .OrderBy(c => c.ConnectionId)
                    .ToList();

                result.Add(new RoomSessionDto
                {
                    RoomId = roomId,
                    ConnectionCount = connections.Count,
                    Connections = connections
                });
            }

            result = result
                .OrderBy(r => r.RoomId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(result);
        }

        /// <summary>
        /// <c>GET /dashboard/v1/sessions/connections</c>: 200 with every active connection (ordered by id) as
        /// <see cref="ConnectionWithRoomDto"/>, including its remote address and room. A connection in several rooms
        /// is reported with the last room enumerated.
        /// </summary>
        [HttpGet("connections")]
        public async Task<ActionResult<IEnumerable<ConnectionWithRoomDto>>> GetAllConnections()
        {
            // Get all known connections
            var allConnectionsDict = await _socketManager.GetAllConnectionsDictAsync();

            // Initialize map: connectionId -> roomId (null by default)
            var connectionRoomMap = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var kvp in allConnectionsDict)
            {
                connectionRoomMap[kvp.Key] = null;
            }

            // Walk all rooms and mark which connection belongs to which room.
            // Assumes a connection is in at most one "primary" room for dashboard purposes.
            var roomsDict = await _socketManager.GetAllRoomsAsync();
            foreach (var roomKvp in roomsDict)
            {
                var roomId = roomKvp.Key;
                var connectionsInRoom = await _socketManager.GetConnectionsInRoomAsync(roomId);

                foreach (var connId in connectionsInRoom.Keys)
                {
                    if (connectionRoomMap.ContainsKey(connId))
                    {
                        connectionRoomMap[connId] = roomId;
                    }
                }
            }

            var result = connectionRoomMap
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp =>
            {
                var conn = allConnectionsDict[kvp.Key];
                return new ConnectionWithRoomDto
                {
                    ConnectionId = kvp.Key,
                    RoomId = kvp.Value,
                    IpAddress = conn.RemoteAddress
                };
            })
            .ToList();

            return Ok(result);
        }

        /// <summary>
        /// <c>DELETE /dashboard/v1/sessions/connections/{connectionId}</c>: disconnects the client (engine-aware) and
        /// removes it from its rooms. 204 on completion (also for an unknown id), 400 when the id is blank.
        /// </summary>
        /// <param name="connectionId">Connection to close.</param>
        [HttpDelete("connections/{connectionId}")]
        public async Task<IActionResult> CloseSession(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
                return BadRequest(new { message = "Connection id is required." });

            // ConnectionManager handles engine-aware disconnect + cleanup.
            await _connectionManager.DisconnectEngineAwareAsync(connectionId);
            _logger.LogInformation("Closed session for connection {ConnectionId} via dashboard.", connectionId);

            return NoContent();
        }

        /// <summary>
        /// <c>DELETE /dashboard/v1/sessions/rooms/{roomId}</c>: disconnects every connection in the room (errors are
        /// logged and skipped), then deletes the room. 204 on completion, 400 when the id is blank.
        /// </summary>
        /// <param name="roomId">Room to delete.</param>
        [HttpDelete("rooms/{roomId}")]
        public async Task<IActionResult> DeleteRoom(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
                return BadRequest(new { message = "Room id is required." });

            var connectionsDict = await _socketManager.GetConnectionsInRoomAsync(roomId);

            // Disconnect all clients in this room first
            foreach (var connectionId in connectionsDict.Keys)
            {
                try
                {
                    await _connectionManager.DisconnectEngineAwareAsync(connectionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error disconnecting connection {ConnectionId} while deleting room {RoomId}.",
                        connectionId, roomId);
                }
            }

            await _socketManager.DeleteRoomAsync(roomId);
            _logger.LogInformation("Deleted room {RoomId} and disconnected {Count} connections via dashboard.",
                roomId, connectionsDict.Count);

            return NoContent();
        }

        /// <summary>
        /// <c>DELETE /dashboard/v1/sessions/rooms/{roomId}/connections/{connectionId}</c>: currently a full disconnect of
        /// the connection (not just leaving the room). 204 on success, 404 when it is not in that room, 400 when an id is blank.
        /// </summary>
        /// <param name="roomId">Room the connection must belong to.</param>
        /// <param name="connectionId">Connection to disconnect.</param>
        [HttpDelete("rooms/{roomId}/connections/{connectionId}")]
        public async Task<IActionResult> RemoveConnectionFromRoom(string roomId, string connectionId)
        {
            if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(connectionId))
                return BadRequest(new { message = "Room id and connection id are required." });

            var connectionsDict = await _socketManager.GetConnectionsInRoomAsync(roomId);
            if (!connectionsDict.ContainsKey(connectionId))
                return NotFound(new { message = $"Connection {connectionId} not found in room {roomId}." });

            await _connectionManager.DisconnectEngineAwareAsync(connectionId);
            _logger.LogInformation("Removed connection {ConnectionId} from room {RoomId} via dashboard.",
                connectionId, roomId);

            return NoContent();
        }
    }

    #region DTOs

    /// <summary>
    /// Represents a room and its active connections for the dashboard.
    /// </summary>
    public sealed class RoomSessionDto
    {
        /// <summary>Room id.</summary>
        public string RoomId { get; set; } = string.Empty;
        /// <summary>Number of connections in the room.</summary>
        public int ConnectionCount { get; set; }
        /// <summary>Connections in the room, ordered by id.</summary>
        public IEnumerable<ConnectionDto> Connections { get; set; } = Array.Empty<ConnectionDto>();
    }

    /// <summary>A connection id inside a <see cref="RoomSessionDto"/>.</summary>
    public sealed class ConnectionDto
    {
        /// <summary>Connection id.</summary>
        public string ConnectionId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a connection with its associated room (if any).
    /// Used by the "all connections" dashboard endpoint.
    /// </summary>
    public sealed class ConnectionWithRoomDto
    {
        /// <summary>Connection id.</summary>
        public string ConnectionId { get; set; } = string.Empty;
        /// <summary>Room the connection is in, or null.</summary>
        public string? RoomId { get; set; }
        /// <summary>Remote address of the connection, if known.</summary>
        public string? IpAddress { get; set; }
    }

    #endregion
}
