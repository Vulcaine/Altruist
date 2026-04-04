using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public struct ServiceHealthResponse
    {
        public bool Online { get; init; }
        public ReadyState ReadyState { get; init; }
        public string Message { get; init; }
    }

    public sealed class ServiceHealthDetailsResponse
    {
        public ReadyState ReadyState { get; init; }
        public bool EngineEnabled { get; init; }

        public Dictionary<string, string> Connectables { get; init; } = new();

        public IReadOnlyCollection<string> Endpoints { get; init; } = Array.Empty<string>();

        public ServerInfo ServerInfo { get; init; } = new("", "", "", 0);
        public string ProcessId { get; init; } = "";
    }

    [ApiController]
    [Route("altruist/health")]
    public class ServiceHealthController : ControllerBase
    {
        private readonly ILogger<ServiceHealthController> _logger;
        private readonly ServerStatus _status;

        public ServiceHealthController(
            ServerStatus status,
            ILogger<ServiceHealthController> logger)
        {
            _logger = logger;
            _status = status;
        }

        [HttpGet("details")]
        public ActionResult<ServiceHealthDetailsResponse> GetDetails(
    [FromServices] IAltruistContext context)
        {
            var details = new ServiceHealthDetailsResponse
            {
                ReadyState = _status.Status,
                EngineEnabled = context.EngineEnabled,
                Endpoints = context.Endpoints,
                ServerInfo = context.ServerInfo,
                ProcessId = context.ProcessId
            };

            foreach (var c in _status.Connectables)
            {
                details.Connectables[c.ServiceName] = c.IsConnected
                    ? "Connected"
                    : "Disconnected";
            }

            return Ok(details);
        }

        [HttpGet]
        public ActionResult<ServiceHealthResponse> Get()
        {
            try
            {
                bool serviceReady = _status.Status == ReadyState.Alive;

                if (!serviceReady)
                {
                    return Ok(new ServiceHealthResponse
                    {
                        Online = false,
                        ReadyState = _status.Status,
                        Message = "Service initializing"
                    });
                }

                return Ok(new ServiceHealthResponse
                {
                    Online = true,
                    ReadyState = _status.Status,
                    Message = "OK"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");

                return StatusCode(503, new ServiceHealthResponse
                {
                    Online = false,
                    ReadyState = ReadyState.Failed,
                    Message = ex.Message
                });
            }
        }
    }
}
