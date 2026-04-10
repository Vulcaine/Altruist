using Microsoft.AspNetCore.Mvc;

namespace AltruistProject.Http;

/// <summary>
/// REST API controller — accessible via http://localhost:8080/api/hello
/// Altruist already provides a built-in /health endpoint automatically.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class HelloController : ControllerBase
{
    /// <summary>GET /api/hello — hello world</summary>
    [HttpGet]
    public IActionResult Hello([FromQuery] string name = "World")
        => Ok(new { message = $"Hello, {name}! Welcome to Altruist." });

    /// <summary>POST /api/hello/echo — echo back the request body</summary>
    [HttpPost("echo")]
    public async Task<IActionResult> Echo()
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        return Ok(new { echo = body, receivedAt = DateTime.UtcNow });
    }
}
