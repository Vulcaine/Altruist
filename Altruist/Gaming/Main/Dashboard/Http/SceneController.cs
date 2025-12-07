using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard
{
    /// <summary>
    /// Dashboard scene controller.
    /// Eventually will return scene data (heightmaps, objects, etc.)
    /// For now, just a stub that returns 204 No Content.
    /// </summary>
    [ApiController]
    [Route("dashboard/v1/scene")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class SceneController : ControllerBase
    {
        /// <summary>
        /// Get scene information for a specific world index.
        /// </summary>
        /// <param name="worldIndex">Index of the world to visualize.</param>
        [HttpGet("{worldIndex:int}")]
        public IActionResult GetScene(int worldIndex)
        {
            // TODO: Hook into IGameWorldOrganizer3D / world index,
            // and return a DTO describing the scene for the dashboard.
            return NoContent();
        }
    }
}
