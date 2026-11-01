using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.DTOs;
using GitHubGateway.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GitHubGateway.Api.Controllers
{
    /// <summary>
    /// IIS-filter-safe way to upload binary artifacts (ZIPs) to a GitHub repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The standard <c>PUT /api/v1/repos/{owner}/{repo}/contents/{path}</c> endpoint is
    /// blocked by IIS Request Filtering whenever the URL contains a dot (<c>.</c>) —
    /// which excludes every file with an extension (<c>.zip</c>, <c>.cs</c>,
    /// <c>.json</c>, ...). The companion <see cref="FileContentController"/> already
    /// works around this for text files via <c>POST /file-content/upload</c>, but its
    /// underlying call <see cref="IGitHubRepositoryService.UpsertFileAsync"/> treats
    /// <see cref="UpdateFileRequest.Content"/> as UTF-8 text, which would corrupt
    /// binary payloads.
    /// </para>
    /// <para>
    /// This controller accepts the file path and base64-encoded binary content inside
    /// the JSON request body. The URL <c>POST /api/v1/repos/{owner}/{repo}/artifacts/upload</c>
    /// has <strong>no file extension</strong>, so IIS Request Filtering does not block it.
    /// Internally it calls <see cref="IGitHubRepositoryService.UpsertBinaryFileAsync"/>
    /// which decodes the base64 and re-encodes for the GitHub Contents API.
    /// </para>
    /// </remarks>
    [ApiController]
    [Route("api/v1/repos/{owner}/{repo}/artifacts")]
    [Produces("application/json")]
    public class ArtifactsController : ControllerBase
    {
        private readonly IGitHubRepositoryService _service;
        public ArtifactsController(IGitHubRepositoryService service) => _service = service;

        /// <summary>
        /// Uploads (creates or updates) a binary file — e.g. a release ZIP — to the
        /// given branch. Pass the file path and base64-encoded content in the request
        /// body so IIS Request Filtering does not see the file extension in the URL.
        /// </summary>
        /// <param name="owner">Repository owner (e.g. <c>ebdavod</c>).</param>
        /// <param name="repo">Repository name (e.g. <c>avalonline-api</c>).</param>
        /// <param name="body">Upload request body.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <response code="200">Upload succeeded.</response>
        /// <response code="400">Bad request — missing or invalid fields.</response>
        [HttpPost("upload")]
        [RequestSizeLimit(125829120)] // 120 MB
        [ProducesResponseType(typeof(CommitResultDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> Upload(string owner, string repo, [FromBody] UploadArtifactRequest body, CancellationToken ct)
        {
            var req = new UpdateBinaryFileRequest
            {
                Owner = owner,
                Repo = repo,
                Path = body?.Path ?? string.Empty,
                Branch = string.IsNullOrWhiteSpace(body?.Branch) ? "main" : body.Branch,
                ContentBase64 = body?.ContentBase64 ?? string.Empty,
                CommitMessage = string.IsNullOrWhiteSpace(body?.CommitMessage)
                    ? $"upload: {body?.Path ?? "(unknown)"}"
                    : body.CommitMessage,
                // If the caller knows the existing sha (e.g. for updates), pass it through.
                // Otherwise leave null — Octokit will reject if the file already exists
                // without a sha, which is the expected GitHub semantics.
                Sha = body?.Sha
            };

            var result = await _service.UpsertBinaryFileAsync(req, ct);
            return Ok(result);
        }

        /// <summary>
        /// Request body for <see cref="Upload"/>.
        /// </summary>
        public sealed class UploadArtifactRequest
        {
            /// <summary>
            /// Full path in the repo, including extension
            /// (e.g. <c>avalonline-api-20261101-210935-6509c5a9.zip</c>).
            /// </summary>
            public string Path { get; set; } = string.Empty;

            /// <summary>Target branch (e.g. <c>deployment</c>).</summary>
            public string Branch { get; set; } = "main";

            /// <summary>
            /// Base64-encoded raw bytes. Whitespace/newlines are tolerated and stripped.
            /// Maximum 80 MB raw (after decode).
            /// </summary>
            public string ContentBase64 { get; set; } = string.Empty;

            /// <summary>Optional commit message. Defaults to <c>"upload: {path}"</c>.</summary>
            public string? CommitMessage { get; set; }

            /// <summary>
            /// Optional SHA of the existing file. Required by GitHub when the file
            /// already exists (update); not required for new files.
            /// </summary>
            public string? Sha { get; set; }
        }
    }
}
