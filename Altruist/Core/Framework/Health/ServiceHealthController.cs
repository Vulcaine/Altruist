using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public struct ServiceHealthResponse
    {
        public bool online;
        public ReadyState readyState;
        public string message;
    }

    [ApiController]
    [Route("health")]
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
                        online = false,
                        readyState = _status.Status,
                        message = "Service initializing"
                    });
                }

                return Ok(new ServiceHealthResponse
                {
                    online = true,
                    readyState = _status.Status,
                    message = "OK"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");

                return StatusCode(503, new ServiceHealthResponse
                {
                    online = false,
                    message = ex.Message
                });
            }
        }
    }
}
