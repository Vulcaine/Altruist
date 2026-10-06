/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Altruist;

/// <summary>
/// The server node for orchestrators and load balancers:
/// <list type="bullet">
/// <item><c>GET /altruist/server/live</c>: 200 while the process runs (liveness probe).</item>
/// <item><c>GET /altruist/server/ready</c>: 200 while it takes new work, 503 when starting, full,
/// draining or drained (readiness probe: a draining server leaves the load balancer).</item>
/// <item><c>GET /altruist/server/capacity</c>: the <see cref="ServerCapacity"/> report (allocators, dashboards).</item>
/// <item><c>POST /altruist/server/drain?wait=true</c>: starts the drain (a Kubernetes preStop hook or
/// an operator); with <c>wait</c> it answers once drained (200) or timed out (202). Allowed from
/// loopback, or with the <c>X-Altruist-Drain-Token</c> header when <c>altruist:server:drain:token</c> is set.</item>
/// </list>
/// </summary>
[ApiController]
[Route("altruist/server")]
public sealed class ServerNodeController : ControllerBase
{
    public const string TokenHeader = "X-Altruist-Drain-Token";

    private readonly IServerNode _node;
    private readonly IConfiguration? _config;

    public ServerNodeController(IServerNode node, IConfiguration? config = null)
    {
        _node = node;
        _config = config;
    }

    [HttpGet("live")]
    public IActionResult Live() => Ok(new { node = _node.NodeId });

    [HttpGet("ready")]
    public IActionResult Ready()
    {
        var state = _node.State;
        var body = new { node = _node.NodeId, state = state.ToString() };
        return state == ServerNodeState.Ready ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }

    [HttpGet("capacity")]
    public ActionResult<ServerCapacity> Capacity() => Ok(_node.Capacity());

    [HttpPost("drain")]
    public async Task<IActionResult> Drain([FromQuery] bool wait = false, [FromQuery] double? timeoutSeconds = null)
    {
        if (!Allowed())
            return StatusCode(StatusCodes.Status403Forbidden);
        if (timeoutSeconds is < 0 or double.NaN)
            return BadRequest();
        var drain = _node.DrainAsync(timeoutSeconds is { } t ? TimeSpan.FromSeconds(t) : null, HttpContext.RequestAborted);
        if (!wait)
            return Accepted(new { node = _node.NodeId, state = _node.State.ToString() });
        var inTime = await drain;
        var body = new { node = _node.NodeId, state = _node.State.ToString(), drained = inTime };
        return inTime ? Ok(body) : Accepted(body);
    }

    private bool Allowed()
    {
        var token = _config?["altruist:server:drain:token"];
        if (!string.IsNullOrEmpty(token))
        {
            var sent = Request.Headers[TokenHeader].ToString();
            return sent.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(token));
        }
        var remote = HttpContext.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote);
    }
}
