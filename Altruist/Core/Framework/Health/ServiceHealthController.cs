using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    /// <summary>Body of <c>GET /altruist/health</c>.</summary>
    public struct ServiceHealthResponse
    {
        /// <summary><c>true</c> once the server reached <see cref="ReadyState.Alive"/>.</summary>
        public bool Online { get; init; }
        /// <summary>Current server readiness.</summary>
        public ReadyState ReadyState { get; init; }
        /// <summary><c>OK</c>, <c>Service initializing</c>, or the error message on failure.</summary>
        public string Message { get; init; }
    }

    /// <summary>Body of <c>GET /altruist/health/details</c>: readiness plus server and dependency diagnostics.</summary>
    public sealed class ServiceHealthDetailsResponse
    {
        /// <summary>Current server readiness.</summary>
        public ReadyState ReadyState { get; init; }
        /// <summary>Whether the game engine loop is enabled for this process.</summary>
        public bool EngineEnabled { get; init; }

        /// <summary>Each tracked <see cref="IConnectable"/> by <see cref="IService.ServiceName"/>: <c>Connected</c> or <c>Disconnected</c>.</summary>
        public Dictionary<string, string> Connectables { get; init; } = new();

        /// <summary>Registered endpoints (portal paths and other routes added to <see cref="IAltruistContext"/>).</summary>
        public IReadOnlyCollection<string> Endpoints { get; init; } = Array.Empty<string>();

        /// <summary>Server identity and address.</summary>
        public ServerInfo ServerInfo { get; init; } = new("", "", "", 0);
        /// <summary>Identifier of this server process.</summary>
        public string ProcessId { get; init; } = "";
    }

    /// <summary>
    /// Built-in health endpoints at <c>altruist/health</c>: <c>GET</c> for a quick liveness/readiness probe and
    /// <c>GET details</c> for diagnostics (readiness, endpoints, dependency connection states).
    /// </summary>
    /// <remarks>
    /// <c>GET /altruist/health</c> answers 200 even while initializing (with <c>Online = false</c>), so probes must check
    /// the body, not just the status code. The details endpoint carries no <c>[Authorize]</c> and exposes internal topology;
    /// restrict it at the network edge in production.
    /// </remarks>
    [ApiController]
    [Route("altruist/health")]
    public class ServiceHealthController : ControllerBase
    {
        private readonly ILogger<ServiceHealthController> _logger;
        private readonly ServerStatus _status;

        /// <summary>Created by ASP.NET Core per request.</summary>
        public ServiceHealthController(
            ServerStatus status,
            ILogger<ServiceHealthController> logger)
        {
            _logger = logger;
            _status = status;
        }

        /// <summary><c>GET /altruist/health/details</c>: readiness, engine flag, endpoints, server info and connectable states.</summary>
        /// <param name="context">The application context (from DI).</param>
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

        /// <summary><c>GET /altruist/health</c>: 200 with <see cref="ServiceHealthResponse.Online"/> true when alive, false while
        /// initializing; 503 if the check itself throws.</summary>
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
