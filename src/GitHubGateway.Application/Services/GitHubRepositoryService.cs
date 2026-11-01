using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Common;
using GitHubGateway.Application.DTOs;
using GitHubGateway.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace GitHubGateway.Application.Services
{
    /// <summary>
    /// Default implementation of <see cref="IGitHubRepositoryService"/>. This class delegates to
    /// the lower-level <see cref="IGitHubGatewayClient"/> (provided by the Infrastructure layer) and adds
    /// application-level concerns: logging, patch application, basic error mapping.
    /// </summary>
    public sealed class GitHubRepositoryService : IGitHubRepositoryService
    {
        private readonly IGitHubGatewayClient _client;
        private readonly ILogger<GitHubRepositoryService> _logger;

        public GitHubRepositoryService(IGitHubGatewayClient client, ILogger<GitHubRepositoryService> logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<ConnectionInfo> GetConnectionInfoAsync(CancellationToken ct = default)
            => _client.GetConnectionInfoAsync(ct);

        public Task<IReadOnlyList<BranchDto>> ListBranchesAsync(string owner, string repo, CancellationToken ct = default)
            => _client.ListBranchesAsync(owner, repo, ct);

        public Task<IReadOnlyList<RepoItemDto>> ListContentsAsync(
            string owner, string repo, string path, string branch, CancellationToken ct = default)
            => _client.ListContentsAsync(owner, repo, path, branch, ct);

        public Task<FileContentDto> GetFileContentAsync(
            string owner, string repo, string path, string branch, CancellationToken ct = default)
            => _client.GetFileContentAsync(owner, repo, path, branch, ct);

        public Task<IReadOnlyList<CommitDto>> ListCommitsAsync(
            string owner, string repo, string path, string branch, int maxCount, CancellationToken ct = default)
            => _client.ListCommitsAsync(owner, repo, path, branch, maxCount, ct);

        public Task<CommitResultDto> UpsertFileAsync(UpdateFileRequest request, CancellationToken ct = default)
            => _client.UpsertFileAsync(request, ct);

        public Task<CommitResultDto> UpsertBinaryFileAsync(UpdateBinaryFileRequest request, CancellationToken ct = default)
            => _client.UpsertBinaryFileAsync(request, ct);

        public Task<CommitResultDto> DeleteFileAsync(DeleteFileRequest request, CancellationToken ct = default)
            => _client.DeleteFileAsync(request, ct);

        public Task<BranchDto> CreateBranchAsync(CreateBranchRequest request, CancellationToken ct = default)
            => _client.CreateBranchAsync(request, ct);

        public Task<PullRequestDto> CreatePullRequestAsync(CreatePullRequestRequest request, CancellationToken ct = default)
            => _client.CreatePullRequestAsync(request, ct);

        public async Task<PatchResultDto> ApplyPatchAsync(ApplyPatchRequest request, CancellationToken ct = default)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));

            var entries = PatchParser.Parse(request.PatchText);
            if (entries.Count == 0)
                throw new InvalidOperationException("Patch contains no file entries.");

            var results = new List<PatchFileResult>();
            string? lastSha = null;

            foreach (var entry in entries)
            {
                string targetPath = !string.IsNullOrEmpty(entry.NewPath) ? entry.NewPath : entry.OldPath;
                try
                {
                    if (entry.IsDeleted)
                    {
                        var file = await _client.GetFileContentAsync(request.Owner, request.Repo, entry.OldPath, request.Branch, ct);
                        var del = await _client.DeleteFileAsync(new DeleteFileRequest
                        {
                            Owner = request.Owner,
                            Repo = request.Repo,
                            Path = entry.OldPath,
                            Branch = request.Branch,
                            Sha = file.Sha,
                            CommitMessage = $"{request.CommitMessage} (delete {entry.OldPath})"
                        }, ct);
                        lastSha = del.CommitSha;
                        results.Add(new PatchFileResult
                        {
                            Path = entry.OldPath,
                            Status = "removed",
                            CommitSha = del.CommitSha
                        });
                    }
                    else
                    {
                        string newContent;
                        string? existingSha = null;
                        string status;

                        if (entry.IsNew)
                        {
                            newContent = PatchParser.ApplyTo(entry, string.Empty);
                            status = "added";
                        }
                        else
                        {
                            var existing = await _client.GetFileContentAsync(request.Owner, request.Repo, entry.OldPath, request.Branch, ct);
                            existingSha = existing.Sha;
                            newContent = PatchParser.ApplyTo(entry, existing.Content ?? string.Empty);
                            status = "modified";
                        }

                        var up = await _client.UpsertFileAsync(new UpdateFileRequest
                        {
                            Owner = request.Owner,
                            Repo = request.Repo,
                            Path = targetPath,
                            Branch = request.Branch,
                            Content = newContent,
                            CommitMessage = $"{request.CommitMessage} ({status} {targetPath})",
                            Sha = existingSha
                        }, ct);
                        lastSha = up.CommitSha;

                        results.Add(new PatchFileResult
                        {
                            Path = targetPath,
                            Status = status,
                            CommitSha = up.CommitSha
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to apply patch entry for {Path}", targetPath);
                    results.Add(new PatchFileResult
                    {
                        Path = targetPath,
                        Status = "error",
                        Error = ex.Message
                    });
                }
            }

            return new PatchResultDto
            {
                Branch = request.Branch,
                CommitSha = lastSha ?? string.Empty,
                Files = results
            };
        }
    }
}
