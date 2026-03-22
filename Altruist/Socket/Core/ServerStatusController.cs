/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
*/

using Altruist.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Altruist.Socket;

/// <summary>
/// Public health endpoint — no auth required.
/// Returns connection count, max, queue length, and server status.
/// </summary>
[ApiController]
[Route("api/v1/server")]
public class ServerStatusController : ControllerBase
{
    private readonly IConnectionGate _gate;

    public ServerStatusController(IConnectionGate gate) => _gate = gate;

    [HttpGet("health")]
    public IActionResult Health()
    {
        var status = _gate.IsFull ? "full" : _gate.ActiveConnections > _gate.MaxConnections * 0.9 ? "busy" : "ok";
        return Ok(new
        {
            status,
            connections = _gate.ActiveConnections,
            max_connections = _gate.MaxConnections,
            queue = _gate.QueueLength,
            available = Math.Max(0, _gate.MaxConnections - _gate.ActiveConnections),
        });
    }
}

/// <summary>
/// Queue status endpoint — requires JWT authentication.
/// Returns the caller's position in the login queue and estimated wait.
/// </summary>
[ApiController]
[Route("api/v1/server")]
[Authorize]
public class QueueStatusController : ControllerBase
{
    private readonly IConnectionGate _gate;

    public QueueStatusController(IConnectionGate gate) => _gate = gate;

    [HttpGet("queue/status")]
    public IActionResult QueueStatus()
    {
        var userId = User.FindFirst("sub")?.Value
                  ?? User.FindFirst("userId")?.Value
                  ?? User.Identity?.Name
                  ?? "";

        if (string.IsNullOrEmpty(userId))
            return Unauthorized("No user identity found.");

        var position = _gate.GetQueuePosition(userId);
        var eta = _gate.EstimatedWaitSeconds(userId);

        if (position <= 0)
        {
            return Ok(new
            {
                status = "not_queued",
                position = 0,
                eta_seconds = 0,
                message = _gate.IsFull ? "Server is full. Join queue to wait." : "Server has capacity. Connect directly.",
            });
        }

        return Ok(new
        {
            status = "queued",
            position,
            eta_seconds = eta,
            message = $"You are #{position} in queue. Estimated wait: {eta}s.",
        });
    }

    [HttpPost("queue/join")]
    public IActionResult JoinQueue()
    {
        var userId = User.FindFirst("sub")?.Value
                  ?? User.FindFirst("userId")?.Value
                  ?? User.Identity?.Name
                  ?? "";

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        if (!_gate.IsFull)
        {
            return Ok(new
            {
                status = "not_needed",
                message = "Server has capacity. Connect directly.",
            });
        }

        // Add to queue (ConnectionGate handles dedup)
        if (_gate is ConnectionGate cg)
            cg.Enqueue(userId);

        return Ok(new
        {
            status = "queued",
            position = _gate.GetQueuePosition(userId),
            eta_seconds = _gate.EstimatedWaitSeconds(userId),
        });
    }

    [HttpDelete("queue/leave")]
    public IActionResult LeaveQueue()
    {
        var userId = User.FindFirst("sub")?.Value
                  ?? User.FindFirst("userId")?.Value
                  ?? User.Identity?.Name
                  ?? "";

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        if (_gate is ConnectionGate cg)
            cg.RemoveFromQueue(userId);

        return Ok(new { status = "left_queue" });
    }
}
