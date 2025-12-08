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
    /// for the live transport (WebSocket / connections).
    /// </summary>
    [ApiController]
    [Route("/dashboard/v1/sessions")]
    public sealed class SessionDashboardController : ControllerBase
    {
        private readonly IConnectionManager _connectionManager;
        private readonly ISocketManager _socketManager;
        private readonly ILogger<SessionDashboardController> _logger;

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
        /// Get all rooms and their active connections.
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
        /// Get all active connections across the server, with their associated room (if any).
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
                .Select(kvp => new ConnectionWithRoomDto
                {
                    ConnectionId = kvp.Key,
                    RoomId = kvp.Value
                })
                .ToList();

            return Ok(result);
        }

        /// <summary>
        /// Close a specific client session (connection).
        /// This will disconnect the client and remove it from any rooms.
        /// </summary>
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
        /// Delete a room and disconnect all its connections.
        /// </summary>
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
        /// Remove a connection from a specific room.
        /// For now we treat this as a full disconnect of that connection.
        /// </summary>
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
        public string RoomId { get; set; } = string.Empty;
        public int ConnectionCount { get; set; }
        public IEnumerable<ConnectionDto> Connections { get; set; } = Array.Empty<ConnectionDto>();
    }

    public sealed class ConnectionDto
    {
        public string ConnectionId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a connection with its associated room (if any).
    /// Used by the "all connections" dashboard endpoint.
    /// </summary>
    public sealed class ConnectionWithRoomDto
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string? RoomId { get; set; }
    }

    #endregion
}
