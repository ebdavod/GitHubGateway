using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Deployment.DTOs;
using GitHubGateway.Application.Deployment.Interfaces;
using GitHubGateway.Application.Deployment.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GitHubGateway.Api.Controllers
{
    /// <summary>
    /// FTP deployment endpoints. All endpoints require the X-API-Key header (enforced by the
    /// IP whitelist middleware, which also handles the API key check).
    /// </summary>
    [ApiController]
    [Route("api/v1/deploy/ftp")]
    [Produces("application/json")]
    public class FtpDeployController : ControllerBase
    {
        private readonly IDeploymentOrchestrator _orchestrator;
        private readonly IFtpClientFactory _ftpFactory;
        private readonly IArtifactFetcher _fetcher;
        private readonly IOptions<FtpDeployOptions> _opts;

        public FtpDeployController(
            IDeploymentOrchestrator orchestrator,
            IFtpClientFactory ftpFactory,
            IArtifactFetcher fetcher,
            IOptions<FtpDeployOptions> opts)
        {
            _orchestrator = orchestrator;
            _ftpFactory = ftpFactory;
            _fetcher = fetcher;
            _opts = opts;
        }

        /// <summary>
        /// Lists all configured deployment targets (without passwords).
        /// </summary>
        [HttpGet("targets")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult ListTargets()
        {
            var result = new System.Collections.Generic.List<TargetInfoDto>();
            foreach (var t in _opts.Value.Targets)
            {
                var hasPassword = !string.IsNullOrEmpty(t.Ftp.Password)
                                  || !string.IsNullOrEmpty(t.Ftp.PasswordEnvVar)
                                  || !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("FtpDeploy__Targets__" + _opts.Value.Targets.IndexOf(t) + "__Ftp__Password"));
                result.Add(new TargetInfoDto
                {
                    Name = t.Name,
                    Owner = t.Owner,
                    Repo = t.Repo,
                    Branch = t.Branch,
                    Enabled = t.Enabled,
                    Host = t.Ftp.Host,
                    Port = t.Ftp.Port,
                    Mode = t.Ftp.Mode,
                    RemoteBasePath = t.Ftp.RemoteBasePath,
                    CreateRemoteBackup = t.Deploy.CreateRemoteBackup,
                    StripRootFolder = t.Deploy.StripRootFolder,
                    UploadExcludePatterns = t.Deploy.UploadExcludePatterns,
                    HasPassword = hasPassword,
                    IsCurrentlyRunning = _orchestrator.IsRunning(t.Name)
                });
            }
            return Ok(result);
        }

        /// <summary>
        /// Returns details of a single target (without password).
        /// </summary>
        [HttpGet("targets/{name}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetTarget(string name)
        {
            var t = _opts.Value.Targets.Find(x => string.Equals(x.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (t == null) return NotFound(new { error = $"Target '{name}' not found." });
            return Ok(new
            {
                name = t.Name,
                owner = t.Owner,
                repo = t.Repo,
                branch = t.Branch,
                enabled = t.Enabled,
                ftp = new
                {
                    host = t.Ftp.Host,
                    port = t.Ftp.Port,
                    mode = t.Ftp.Mode,
                    remoteBasePath = t.Ftp.RemoteBasePath,
                    passiveMode = t.Ftp.PassiveMode,
                    encryptionMode = t.Ftp.EncryptionMode,
                    timeoutSeconds = t.Ftp.TimeoutSeconds,
                    hasPassword = !string.IsNullOrEmpty(t.Ftp.Password) || !string.IsNullOrEmpty(t.Ftp.PasswordEnvVar),
                    passwordEnvVar = t.Ftp.PasswordEnvVar
                },
                deploy = new
                {
                    createRemoteBackup = t.Deploy.CreateRemoteBackup,
                    backupSuffix = t.Deploy.BackupSuffix,
                    uploadExcludePatterns = t.Deploy.UploadExcludePatterns,
                    backupExcludePatterns = t.Deploy.BackupExcludePatterns,
                    stripRootFolder = t.Deploy.StripRootFolder,
                    overwriteExisting = t.Deploy.OverwriteExisting,
                    maxParallelUploads = t.Deploy.MaxParallelUploads
                },
                isRunning = _orchestrator.IsRunning(t.Name)
            });
        }

        /// <summary>
        /// Tests the FTP connection and returns the root directory listing.
        /// </summary>
        [HttpPost("targets/{name}/test-connection")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> TestConnection(string name, [FromQuery] int maxDepth = 1, [FromBody] object? body = null, CancellationToken ct = default)
        {
            var t = _opts.Value.Targets.Find(x => string.Equals(x.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (t == null) return NotFound(new { error = $"Target '{name}' not found." });

            try
            {
                using var ftp = _ftpFactory.Create(t.Ftp);
                await ftp.ConnectAsync(ct);
                var tree = await ftp.GetTreeAsync(t.Ftp.RemoteBasePath, maxDepth, ct);
                await ftp.DisconnectAsync(ct);
                return Ok(new FtpTestResult
                {
                    Success = true,
                    Host = t.Ftp.Host,
                    Port = t.Ftp.Port,
                    Mode = t.Ftp.Mode,
                    RootListing = tree
                });
            }
            catch (System.Exception ex)
            {
                return Ok(new FtpTestResult
                {
                    Success = false,
                    Host = t.Ftp.Host,
                    Port = t.Ftp.Port,
                    Mode = t.Ftp.Mode,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Lists the remote folder structure of the target.
        /// </summary>
        [HttpGet("targets/{name}/remote-tree")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetRemoteTree(string name,
            [FromQuery] string path = "",
            [FromQuery] int maxDepth = 2,
            CancellationToken ct = default)
        {
            var t = _opts.Value.Targets.Find(x => string.Equals(x.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (t == null) return NotFound(new { error = $"Target '{name}' not found." });

            var target = string.IsNullOrEmpty(path) ? t.Ftp.RemoteBasePath : path;
            using var ftp = _ftpFactory.Create(t.Ftp);
            await ftp.ConnectAsync(ct);
            var tree = await ftp.GetTreeAsync(target, maxDepth, ct);
            await ftp.DisconnectAsync(ct);
            return Ok(tree);
        }

        /// <summary>
        /// Lists all artifacts in the deployment branch.
        /// </summary>
        [HttpGet("targets/{name}/artifacts")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> ListArtifacts(string name, CancellationToken ct)
        {
            var t = _opts.Value.Targets.Find(x => string.Equals(x.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (t == null) return NotFound(new { error = $"Target '{name}' not found." });
            var arts = await _fetcher.ListArtifactsAsync(t.Owner, t.Repo, t.Branch, ct);
            return Ok(arts);
        }

        /// <summary>
        /// Triggers a deployment. Returns the JobId immediately (the work runs in background).
        /// </summary>
        [HttpPost("targets/{name}/deploy")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Deploy(string name, [FromBody] TriggerDeployRequest? request, CancellationToken ct)
        {
            var t = _opts.Value.Targets.Find(x => string.Equals(x.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (t == null) return NotFound(new { error = $"Target '{name}' not found." });

            try
            {
                var job = await _orchestrator.TriggerAsync(name, request, ct);
                return Ok(job);
            }
            catch (System.InvalidOperationException ex)
            {
                return Conflict(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Returns the live status of a deployment job (or the latest job if jobId is omitted).
        /// </summary>
        [HttpGet("targets/{name}/status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetStatus(string name, [FromQuery] string? jobId, CancellationToken ct)
        {
            var t = _opts.Value.Targets.Find(x => string.Equals(x.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (t == null) return NotFound(new { error = $"Target '{name}' not found." });
            var status = await _orchestrator.GetStatusAsync(name, jobId, ct);
            return Ok(status ?? (object)new { state = "idle" });
        }

        /// <summary>
        /// Returns the history of completed deployments.
        /// </summary>
        [HttpGet("targets/{name}/history")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetHistory(string name,
            CancellationToken ct,
            [FromQuery] int maxCount = 10)
        {
            var t = _opts.Value.Targets.Find(x => string.Equals(x.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (t == null) return NotFound(new { error = $"Target '{name}' not found." });
            var history = await _orchestrator.GetHistoryAsync(name, maxCount, ct);
            return Ok(history);
        }
    }
}
