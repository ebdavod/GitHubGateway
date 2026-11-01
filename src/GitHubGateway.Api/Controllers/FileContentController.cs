using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.DTOs;
using GitHubGateway.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GitHubGateway.Api.Controllers
{
    /// <summary>
    /// Workaround for IIS Request Filtering that blocks PUT requests with paths containing dots.
    /// This controller accepts file uploads via POST with the path in the JSON body, completely
    /// bypassing IIS file-extension filtering.
    /// </summary>
    [ApiController]
    [Route("api/v1/repos/{owner}/{repo}/file-content")]
    [Produces("application/json")]
    public class FileContentController : ControllerBase
    {
        private readonly IGitHubRepositoryService _service;
        public FileContentController(IGitHubRepositoryService service) => _service = service;

        /// <summary>
        /// Fetches the content of a file. Pass the path in the request body to bypass IIS dot-blocking.
        /// </summary>
        [HttpPost("get")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetFileContent(string owner, string repo, [FromBody] GetFileContentRequest body, CancellationToken ct)
        {
            var result = await _service.GetFileContentAsync(owner, repo, body?.Path ?? string.Empty, body?.Branch ?? "main", ct);
            return Ok(result);
        }

        /// <summary>
        /// Lists the contents of a directory. Pass the path in the request body to bypass IIS dot-blocking.
        /// </summary>
        [HttpPost("list")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> ListContents(string owner, string repo, [FromBody] ListContentsRequest body, CancellationToken ct)
        {
            var result = await _service.ListContentsAsync(owner, repo, body?.Path ?? string.Empty, body?.Branch ?? "main", ct);
            return Ok(result);
        }

        /// <summary>
        /// Uploads (creates or updates) a file. Pass the path and content in the request body
        /// to bypass IIS PUT-blocking on paths with dots.
        /// </summary>
        [HttpPost("upload")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> UploadFile(string owner, string repo, [FromBody] UploadFileRequest body, CancellationToken ct)
        {
            var req = new UpdateFileRequest
            {
                Owner = owner,
                Repo = repo,
                Path = body?.Path ?? string.Empty,
                Branch = body?.Branch ?? "main",
                Content = body?.Content ?? string.Empty,
                CommitMessage = body?.CommitMessage ?? "Update via gateway",
                Sha = body?.Sha
            };
            var result = await _service.UpsertFileAsync(req, ct);
            return Ok(result);
        }

        /// <summary>
        /// Deletes a file. Pass the path in the request body.
        /// </summary>
        [HttpPost("delete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteFile(string owner, string repo, [FromBody] DeleteFileRequestBody body, CancellationToken ct)
        {
            var req = new DeleteFileRequest
            {
                Owner = owner,
                Repo = repo,
                Path = body?.Path ?? string.Empty,
                Branch = body?.Branch ?? "main",
                Sha = body?.Sha ?? string.Empty,
                CommitMessage = body?.CommitMessage ?? "Delete via gateway"
            };
            var result = await _service.DeleteFileAsync(req, ct);
            return Ok(result);
        }

        public sealed class GetFileContentRequest
        {
            public string Path { get; set; } = string.Empty;
            public string Branch { get; set; } = "main";
        }

        public sealed class ListContentsRequest
        {
            public string Path { get; set; } = string.Empty;
            public string Branch { get; set; } = "main";
        }

        public sealed class UploadFileRequest
        {
            public string Path { get; set; } = string.Empty;
            public string Branch { get; set; } = "main";
            /// <summary>File content. For binary, use base64-encoded string.</summary>
            public string Content { get; set; } = string.Empty;
            public string CommitMessage { get; set; } = "Update via gateway";
            /// <summary>Required for updates; null for new files.</summary>
            public string? Sha { get; set; }
        }

        public sealed class DeleteFileRequestBody
        {
            public string Path { get; set; } = string.Empty;
            public string Branch { get; set; } = "main";
            public string Sha { get; set; } = string.Empty;
            public string CommitMessage { get; set; } = "Delete via gateway";
        }
    }
}
