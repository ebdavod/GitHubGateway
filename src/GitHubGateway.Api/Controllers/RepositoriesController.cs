using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.DTOs;
using GitHubGateway.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GitHubGateway.Api.Controllers
{
    /// <summary>
    /// Read / write operations against GitHub repositories through the gateway.
    /// </summary>
    [ApiController]
    [Route("api/v1/repos/{owner}/{repo}")]
    [Produces("application/json")]
    public class RepositoriesController : ControllerBase
    {
        private readonly IGitHubRepositoryService _service;
        public RepositoriesController(IGitHubRepositoryService service) => _service = service;

        // ---------- READ ----------

        /// <summary>Lists all branches of the repository.</summary>
        [HttpGet("branches")]
        [ProducesResponseType(typeof(IReadOnlyList<BranchDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ListBranches(string owner, string repo, CancellationToken ct)
            => Ok(await _service.ListBranchesAsync(owner, repo, ct));

        /// <summary>Lists the contents (files and sub-directories) at the given path on the given branch.</summary>
        [HttpGet("contents/{*path}")]
        [ProducesResponseType(typeof(IReadOnlyList<RepoItemDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(FileContentDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetContents(
            string owner, string repo, string? path,
            [FromQuery] string branch = "main",
            [FromQuery] bool metadataOnly = false,
            CancellationToken ct = default)
        {
            path ??= string.Empty;
            if (metadataOnly)
            {
                var items = await _service.ListContentsAsync(owner, repo, path, branch, ct);
                return Ok(items);
            }

            // Try as a directory first, fall back to a single file.
            try
            {
                var items = await _service.ListContentsAsync(owner, repo, path, branch, ct);
                return Ok(items);
            }
            catch
            {
                var file = await _service.GetFileContentAsync(owner, repo, path, branch, ct);
                return Ok(file);
            }
        }

        /// <summary>Returns the raw file content at the given path on the given branch.</summary>
        [HttpGet("file/{*path}")]
        [ProducesResponseType(typeof(FileContentDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetFile(string owner, string repo, string path,
            [FromQuery] string branch = "main", CancellationToken ct = default)
            => Ok(await _service.GetFileContentAsync(owner, repo, path, branch, ct));

        /// <summary>Returns the latest commits for the repository or for a specific path.</summary>
        [HttpGet("commits")]
        [ProducesResponseType(typeof(IReadOnlyList<CommitDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ListCommits(string owner, string repo,
            [FromQuery] string? path = null,
            [FromQuery] string branch = "main",
            [FromQuery] int maxCount = 30,
            CancellationToken ct = default)
            => Ok(await _service.ListCommitsAsync(owner, repo, path ?? string.Empty, branch, maxCount, ct));

        // ---------- WRITE ----------

        /// <summary>Creates or updates a file on the given branch. SHA is required for updates.</summary>
        [HttpPut("contents/{*path}")]
        [ProducesResponseType(typeof(CommitResultDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> UpsertFile(string owner, string repo, string path,
            [FromBody] UpdateFileRequestBody body, CancellationToken ct)
        {
            var req = new UpdateFileRequest
            {
                Owner = owner,
                Repo = repo,
                Path = path,
                Branch = body?.Branch ?? "main",
                Content = body?.Content ?? string.Empty,
                CommitMessage = body?.CommitMessage ?? "Update via gateway",
                Sha = body?.Sha
            };
            return Ok(await _service.UpsertFileAsync(req, ct));
        }

        /// <summary>Deletes a file. SHA is required (use GET /file/{path} to obtain it).</summary>
        [HttpDelete("contents/{*path}")]
        [ProducesResponseType(typeof(CommitResultDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteFile(string owner, string repo, string path,
            [FromQuery] string branch = "main",
            [FromQuery] string sha = "",
            [FromQuery] string commitMessage = "Delete via gateway",
            CancellationToken ct = default)
        {
            var req = new DeleteFileRequest
            {
                Owner = owner,
                Repo = repo,
                Path = path,
                Branch = branch,
                Sha = sha,
                CommitMessage = commitMessage
            };
            return Ok(await _service.DeleteFileAsync(req, ct));
        }

        /// <summary>Creates a new branch from an existing branch.</summary>
        [HttpPost("branches")]
        [ProducesResponseType(typeof(BranchDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> CreateBranch(string owner, string repo,
            [FromBody] CreateBranchRequestBody body, CancellationToken ct)
        {
            var req = new CreateBranchRequest
            {
                Owner = owner,
                Repo = repo,
                NewBranchName = body?.NewBranchName ?? "feature/new-branch",
                FromBranch = body?.FromBranch ?? "main"
            };
            return Ok(await _service.CreateBranchAsync(req, ct));
        }

        /// <summary>Creates a Pull Request.</summary>
        [HttpPost("pulls")]
        [ProducesResponseType(typeof(PullRequestDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> CreatePullRequest(string owner, string repo,
            [FromBody] CreatePullRequestRequestBody body, CancellationToken ct)
        {
            var req = new CreatePullRequestRequest
            {
                Owner = owner,
                Repo = repo,
                Title = body?.Title ?? "Untitled PR",
                Head = body?.Head ?? "main",
                Base = body?.Base ?? "main",
                Body = body?.Body ?? string.Empty,
                Draft = body?.Draft ?? false
            };
            return Ok(await _service.CreatePullRequestAsync(req, ct));
        }

        /// <summary>
        /// Applies a unified git patch to the given branch. The patch is parsed by the server,
        /// each file is fetched, modified, and committed sequentially.
        /// </summary>
        [HttpPost("patch")]
        [ProducesResponseType(typeof(PatchResultDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> ApplyPatch(string owner, string repo,
            [FromBody] ApplyPatchRequestBody body, CancellationToken ct)
        {
            var req = new ApplyPatchRequest
            {
                Owner = owner,
                Repo = repo,
                Branch = body?.Branch ?? "main",
                PatchText = body?.PatchText ?? string.Empty,
                CommitMessage = body?.CommitMessage ?? "Apply patch via gateway"
            };
            return Ok(await _service.ApplyPatchAsync(req, ct));
        }

        // ---------- Request bodies (kept here to keep controllers self-contained) ----------

        public record UpdateFileRequestBody
        {
            public string Branch { get; init; } = "main";
            public string Content { get; init; } = string.Empty;
            public string CommitMessage { get; init; } = string.Empty;
            public string? Sha { get; init; }
        }

        public record CreateBranchRequestBody
        {
            public string NewBranchName { get; init; } = string.Empty;
            public string FromBranch { get; init; } = "main";
        }

        public record CreatePullRequestRequestBody
        {
            public string Title { get; init; } = string.Empty;
            public string Head { get; init; } = string.Empty;
            public string Base { get; init; } = string.Empty;
            public string Body { get; init; } = string.Empty;
            public bool Draft { get; init; }
        }

        public record ApplyPatchRequestBody
        {
            public string Branch { get; init; } = "main";
            public string PatchText { get; init; } = string.Empty;
            public string CommitMessage { get; init; } = string.Empty;
        }
    }
}
