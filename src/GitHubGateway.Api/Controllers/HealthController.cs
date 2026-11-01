using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GitHubGateway.Api.Controllers
{
    /// <summary>
    /// Health and info endpoints. These are exempt from the API-key check when
    /// <see cref="GitHubGateway.Application.Common.IpWhitelistOptions.SkipApiKeyForHealthEndpoints"/>
    /// is true (default).
    /// </summary>
    [ApiController]
    [Route("api")]
    public class HealthController : ControllerBase
    {
        private readonly IGitHubRepositoryService _service;
        public HealthController(IGitHubRepositoryService service) => _service = service;

        /// <summary>Liveness probe — always returns 200 if the process is up.</summary>
        [HttpGet("health/live")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult Live() => Ok(new { status = "healthy", service = "GitHubGateway" });

        /// <summary>Readiness probe — verifies that the GitHub PAT is valid and the API is reachable.</summary>
        [HttpGet("health/ready")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Ready(CancellationToken ct)
        {
            var info = await _service.GetConnectionInfoAsync(ct);
            if (info.Authenticated)
                return Ok(new { status = "ready", service = "GitHubGateway", github = info });
            return StatusCode(503, new { status = "unavailable", github = info });
        }

        /// <summary>Returns metadata about the connection (user, plan, rate limit).</summary>
        [HttpGet("info")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> Info(CancellationToken ct)
            => Ok(await _service.GetConnectionInfoAsync(ct));
    }
}
