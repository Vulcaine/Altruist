using Microsoft.AspNetCore.Mvc;

namespace AltruistProject.Http;

/// <summary>
/// REST API controller — accessible via HTTP at http://localhost:8080/api/health
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    /// <summary>GET /api/health — server status check</summary>
    [HttpGet]
    public IActionResult GetHealth() => Ok(new
    {
        status = "ok",
        uptime = Environment.TickCount64 / 1000,
        timestamp = DateTime.UtcNow
    });

    /// <summary>GET /api/health/hello?name=World — hello world endpoint</summary>
    [HttpGet("hello")]
    public IActionResult Hello([FromQuery] string name = "World")
        => Ok(new { message = $"Hello, {name}! Welcome to Altruist." });

    /// <summary>POST /api/health/echo — echo back the request body</summary>
    [HttpPost("echo")]
    public async Task<IActionResult> Echo()
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        return Ok(new { echo = body, receivedAt = DateTime.UtcNow });
    }
}
