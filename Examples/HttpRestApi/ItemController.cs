using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

namespace HttpRestApi;

/// <summary>
/// Simple CRUD controller with in-memory storage.
/// Altruist auto-discovers [ApiController] classes.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ItemsController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, ItemDto> _items = new();

    [HttpGet]
    public IActionResult GetAll()
        => Ok(_items.Values.ToList());

    [HttpGet("{id}")]
    public IActionResult Get(string id)
        => _items.TryGetValue(id, out var item) ? Ok(item) : NotFound();

    [HttpPost]
    public IActionResult Create([FromBody] ItemDto item)
    {
        item.Id = Guid.NewGuid().ToString("N")[..8];
        _items[item.Id] = item;
        return CreatedAtAction(nameof(Get), new { id = item.Id }, item);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
        => _items.TryRemove(id, out _) ? NoContent() : NotFound();
}

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
        => Ok(new { status = "ok", uptime = Environment.TickCount64 / 1000 });
}

public class ItemDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Value { get; set; }
}
