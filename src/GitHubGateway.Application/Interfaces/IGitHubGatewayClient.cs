using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.DTOs;

namespace GitHubGateway.Application.Interfaces
{
    /// <summary>
    /// Low-level port abstracting the GitHub REST API / Octokit SDK.
    /// The Application layer depends on this interface; the Infrastructure layer provides
    /// the concrete implementation that talks to GitHub.
    ///
    /// NOTE: This is intentionally NOT named IGitHubClient to avoid clashing with the Octokit
    /// IGitHubClient type that the infrastructure layer consumes.
    /// </summary>
    public interface IGitHubGatewayClient
    {
        Task<ConnectionInfo> GetConnectionInfoAsync(CancellationToken ct = default);
        Task<IReadOnlyList<BranchDto>> ListBranchesAsync(string owner, string repo, CancellationToken ct = default);
        Task<IReadOnlyList<RepoItemDto>> ListContentsAsync(string owner, string repo, string path, string branch, CancellationToken ct = default);
        Task<FileContentDto> GetFileContentAsync(string owner, string repo, string path, string branch, CancellationToken ct = default);
        Task<IReadOnlyList<CommitDto>> ListCommitsAsync(string owner, string repo, string path, string branch, int maxCount, CancellationToken ct = default);
        Task<CommitResultDto> UpsertFileAsync(UpdateFileRequest request, CancellationToken ct = default);
        Task<CommitResultDto> DeleteFileAsync(DeleteFileRequest request, CancellationToken ct = default);
        Task<BranchDto> CreateBranchAsync(CreateBranchRequest request, CancellationToken ct = default);
        Task<PullRequestDto> CreatePullRequestAsync(CreatePullRequestRequest request, CancellationToken ct = default);

        /// <summary>
        /// Create-or-update a <strong>binary</strong> file (e.g. a release ZIP).
        /// Unlike <see cref="UpsertFileAsync"/> (which treats <see cref="UpdateFileRequest.Content"/>
        /// as UTF-8 text), this method takes raw bytes and base64-encodes them internally
        /// before sending to GitHub — exactly what the GitHub Contents API expects for
        /// binary files.
        /// </summary>
        /// <remarks>
        /// Use this from the <c>POST /api/v1/repos/{owner}/{repo}/artifacts/upload</c>
        /// endpoint, which is the IIS-filter-safe path for uploading release ZIPs
        /// (the URL has no file extension, so IIS Request Filtering does not block it).
        /// </remarks>
        Task<CommitResultDto> UpsertBinaryFileAsync(UpdateBinaryFileRequest request, CancellationToken ct = default);
    }
}
