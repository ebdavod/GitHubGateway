using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.DTOs;
using GitHubGateway.Application.Interfaces;

namespace GitHubGateway.Application.Interfaces
{
    /// <summary>
    /// Application-level service used by the API controllers. Adds cross-cutting concerns
    /// (logging, patch application) on top of <see cref="IGitHubGatewayClient"/>.
    /// </summary>
    public interface IGitHubRepositoryService
    {
        Task<ConnectionInfo> GetConnectionInfoAsync(CancellationToken ct = default);
        Task<System.Collections.Generic.IReadOnlyList<BranchDto>> ListBranchesAsync(string owner, string repo, CancellationToken ct = default);
        Task<System.Collections.Generic.IReadOnlyList<RepoItemDto>> ListContentsAsync(string owner, string repo, string path, string branch, CancellationToken ct = default);
        Task<FileContentDto> GetFileContentAsync(string owner, string repo, string path, string branch, CancellationToken ct = default);
        Task<System.Collections.Generic.IReadOnlyList<CommitDto>> ListCommitsAsync(string owner, string repo, string path, string branch, int maxCount, CancellationToken ct = default);
        Task<CommitResultDto> UpsertFileAsync(UpdateFileRequest request, CancellationToken ct = default);
        Task<CommitResultDto> DeleteFileAsync(DeleteFileRequest request, CancellationToken ct = default);
        Task<BranchDto> CreateBranchAsync(CreateBranchRequest request, CancellationToken ct = default);
        Task<PullRequestDto> CreatePullRequestAsync(CreatePullRequestRequest request, CancellationToken ct = default);
        Task<PatchResultDto> ApplyPatchAsync(ApplyPatchRequest request, CancellationToken ct = default);

        /// <summary>
        /// Create-or-update a binary file (e.g. a release ZIP). The content is supplied
        /// base64-encoded inside <see cref="UpdateBinaryFileRequest.ContentBase64"/>.
        /// Use this from the <c>POST /api/v1/repos/{owner}/{repo}/artifacts/upload</c>
        /// endpoint, which is the IIS-filter-safe path for uploading release ZIPs.
        /// </summary>
        Task<CommitResultDto> UpsertBinaryFileAsync(UpdateBinaryFileRequest request, CancellationToken ct = default);
    }
}
