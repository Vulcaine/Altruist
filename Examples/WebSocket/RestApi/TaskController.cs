using System.Collections.Concurrent;
using Altruist;
using Microsoft.AspNetCore.Mvc;
using RestApi.Packets;

namespace RestApi;

public class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[ApiController]
[Route("api/tasks")]
public class TaskController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, TaskItem> _tasks = new();
    private readonly IAltruistRouter _router;

    public TaskController(IAltruistRouter router)
    {
        _router = router;
    }

    [HttpGet]
    public ActionResult<IEnumerable<TaskItem>> GetAll()
    {
        return Ok(_tasks.Values.OrderByDescending(t => t.CreatedAt));
    }

    [HttpGet("{id}")]
    public ActionResult<TaskItem> GetById(string id)
    {
        if (_tasks.TryGetValue(id, out var task))
            return Ok(task);
        return NotFound();
    }

    [HttpPost]
    public async Task<ActionResult<TaskItem>> Create([FromBody] TaskItem task)
    {
        task.Id = Guid.NewGuid().ToString("N")[..8];
        task.CreatedAt = DateTime.UtcNow;
        _tasks[task.Id] = task;

        // Notify all connected WebSocket clients
        await _router.Broadcast.SendAsync(new STaskNotification
        {
            MessageCode = NotifyCodes.TaskCreated,
            TaskId = task.Id,
            Title = task.Title,
        });

        return CreatedAtAction(nameof(GetById), new { id = task.Id }, task);
    }

    [HttpPut("{id}/complete")]
    public async Task<ActionResult> Complete(string id)
    {
        if (!_tasks.TryGetValue(id, out var task))
            return NotFound();

        task.Completed = true;

        await _router.Broadcast.SendAsync(new STaskNotification
        {
            MessageCode = NotifyCodes.TaskCompleted,
            TaskId = task.Id,
            Title = task.Title,
        });

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id)
    {
        if (!_tasks.TryRemove(id, out var task))
            return NotFound();

        await _router.Broadcast.SendAsync(new STaskNotification
        {
            MessageCode = NotifyCodes.TaskDeleted,
            TaskId = task.Id,
            Title = task.Title,
        });

        return NoContent();
    }
}
