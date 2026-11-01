using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Common;
using GitHubGateway.Application.DTOs;
using GitHubGateway.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using OctokitUpdateFileRequest = Octokit.UpdateFileRequest;
using OctokitDeleteFileRequest = Octokit.DeleteFileRequest;

namespace GitHubGateway.Infrastructure.GitHub
{
    /// <summary>
    /// Octokit-backed implementation of <see cref="IGitHubGatewayClient"/>.
    /// Uses an in-memory cache for read operations, surfaces GitHub errors as
    /// <see cref="GitHubGatewayException"/> with appropriate HTTP status codes, and
    /// automatically invalidates cached entries on write operations.
    /// </summary>
    public sealed class OctokitGitHubClient : IGitHubGatewayClient
    {
        private readonly GitHubClient _octokit;
        private readonly GitHubOptions _options;
        private readonly IMemoryCache _cache;
        private readonly ILogger<OctokitGitHubClient> _logger;

        public OctokitGitHubClient(
            GitHubClient octokit,
            IOptions<GitHubOptions> options,
            IMemoryCache cache,
            ILogger<OctokitGitHubClient> logger)
        {
            _octokit = octokit ?? throw new ArgumentNullException(nameof(octokit));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ConnectionInfo> GetConnectionInfoAsync(CancellationToken ct = default)
        {
            try
            {
                var user = await _octokit.User.Current();
                var last = _octokit.GetLastApiInfo();
                return new ConnectionInfo
                {
                    Authenticated = true,
                    UserName = user?.Login,
                    PlanName = user?.Plan?.Name,
                    RateLimitRemaining = last?.RateLimit?.Remaining ?? 0,
                    RateLimitTotal = last?.RateLimit?.Limit ?? 0,
                    RateLimitResetUnixSeconds = last?.RateLimit?.ResetAsUtcEpochSeconds ?? 0
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch GitHub connection info.");
                return new ConnectionInfo { Authenticated = false };
            }
        }

        public async Task<IReadOnlyList<BranchDto>> ListBranchesAsync(string owner, string repo, CancellationToken ct = default)
        {
            string key = $"branches:{owner}/{repo}";
            if (_cache.TryGetValue(key, out IReadOnlyList<BranchDto>? cached) && cached != null)
                return cached;

            try
            {
                var branches = await _octokit.Repository.Branch.GetAll(owner, repo);
                var dtos = branches.Select(b => new BranchDto
                {
                    Name = b.Name,
                    CommitSha = b.Commit?.Sha ?? string.Empty,
                    Protected = b.Protected
                }).ToList();
                _cache.Set(key, dtos, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
                return dtos;
            }
            catch (NotFoundException ex) { throw MapError(ex, $"Repository {owner}/{repo} not found."); }
            catch (Exception ex) { throw MapError(ex); }
        }

        public async Task<IReadOnlyList<RepoItemDto>> ListContentsAsync(
            string owner, string repo, string path, string branch, CancellationToken ct = default)
        {
            path ??= string.Empty;
            string key = $"contents:{owner}/{repo}:{branch}:{path}";
            if (_cache.TryGetValue(key, out IReadOnlyList<RepoItemDto>? cached) && cached != null)
                return cached;

            try
            {
                var contents = string.IsNullOrEmpty(path)
                    ? await _octokit.Repository.Content.GetAllContentsByRef(owner, repo, branch)
                    : await _octokit.Repository.Content.GetAllContentsByRef(owner, repo, path, branch);

                var dtos = contents.Select(c => new RepoItemDto
                {
                    Name = c.Name,
                    Path = c.Path,
                    Type = c.Type.StringValue,
                    Size = c.Size,
                    Sha = c.Sha,
                    HtmlUrl = c.HtmlUrl
                }).ToList();
                _cache.Set(key, dtos, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
                return dtos;
            }
            catch (NotFoundException ex) { throw MapError(ex, $"Path '{path}' not found in {owner}/{repo}."); }
            catch (Exception ex) { throw MapError(ex); }
        }

        public async Task<FileContentDto> GetFileContentAsync(
            string owner, string repo, string path, string branch, CancellationToken ct = default)
        {
            string key = $"file:{owner}/{repo}:{branch}:{path}";
            if (_cache.TryGetValue(key, out FileContentDto? cached) && cached != null)
                return cached;

            try
            {
                // Get the metadata (returns a list with one entry for a file)
                var meta = await _octokit.Repository.Content.GetAllContentsByRef(owner, repo, path, branch);
                var entry = meta is { Count: > 0 } ? meta[0] : null;

                if (entry == null || entry.Type != Octokit.ContentType.File)
                    throw new GitHubGatewayException($"Path '{path}' is not a file.", HttpStatusCode.NotFound);

                // Download raw content
                var raw = await _octokit.Repository.Content.GetRawContentByRef(owner, repo, path, branch);
                string content;
                try { content = System.Text.Encoding.UTF8.GetString(raw); }
                catch { content = Convert.ToBase64String(raw); }

                var dto = new FileContentDto
                {
                    Name = entry.Name,
                    Path = entry.Path,
                    Sha = entry.Sha,
                    Size = raw.Length,
                    Type = "file",
                    Content = content,
                    Encoding = "utf-8",
                    HtmlUrl = entry.HtmlUrl,
                    DownloadUrl = entry.DownloadUrl ?? string.Empty
                };
                _cache.Set(key, dto, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
                return dto;
            }
            catch (NotFoundException ex) { throw MapError(ex, $"File '{path}' not found in {owner}/{repo}@{branch}."); }
            catch (GitHubGatewayException) { throw; }
            catch (Exception ex) { throw MapError(ex); }
        }

        public async Task<IReadOnlyList<CommitDto>> ListCommitsAsync(
            string owner, string repo, string path, string branch, int maxCount, CancellationToken ct = default)
        {
            try
            {
                var req = new CommitRequest { Path = path, Sha = branch };
                var commits = await _octokit.Repository.Commit.GetAll(owner, repo, req,
                    new ApiOptions { PageSize = Math.Min(Math.Max(maxCount, 1), 100), PageCount = 1 });
                return commits.Take(maxCount).Select(c => new CommitDto
                {
                    Sha = c.Sha,
                    Message = c.Commit?.Message ?? string.Empty,
                    Author = c.Commit?.Author?.Name ?? c.Author?.Login ?? "unknown",
                    Date = c.Commit?.Author?.Date ?? DateTimeOffset.UtcNow,
                    HtmlUrl = c.HtmlUrl ?? string.Empty
                }).ToList();
            }
            catch (NotFoundException ex) { throw MapError(ex, $"Repository {owner}/{repo} not found."); }
            catch (Exception ex) { throw MapError(ex); }
        }

        public async Task<CommitResultDto> UpsertFileAsync(GitHubGateway.Application.DTOs.UpdateFileRequest request, CancellationToken ct = default)
        {
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(request.Content);
                var base64 = Convert.ToBase64String(bytes);

                // Decide create vs update — Octokit v14's UpdateFileRequest constructor
                // rejects both null and empty-string for sha, so we cannot pass "no sha"
                // for new files. We look up the file first; if it doesn't exist (404),
                // we use CreateFile instead.
                string? sha = request.Sha;
                bool useCreate = false;
                if (string.IsNullOrEmpty(sha))
                {
                    try
                    {
                        var existing = await _octokit.Repository.Content.GetAllContentsByRef(
                            request.Owner, request.Repo, request.Path, request.Branch);
                        sha = existing.Count > 0 ? existing[0].Sha : null;
                        useCreate = string.IsNullOrEmpty(sha);
                    }
                    catch (NotFoundException)
                    {
                        useCreate = true;
                    }
                }

                Octokit.RepositoryContentChangeSet changeSet;
                if (useCreate)
                {
                    var createReq = new CreateFileRequest(
                        request.CommitMessage, base64, request.Branch, convertContentToBase64: false);
                    changeSet = await _octokit.Repository.Content.CreateFile(
                        request.Owner, request.Repo, request.Path, createReq);
                }
                else
                {
                    var updateReq = new OctokitUpdateFileRequest(
                        request.CommitMessage, base64, sha ?? string.Empty, request.Branch);
                    changeSet = await _octokit.Repository.Content.UpdateFile(
                        request.Owner, request.Repo, request.Path, updateReq);
                }

                Invalidate(request.Owner, request.Repo, request.Branch, request.Path);
                return new CommitResultDto
                {
                    CommitSha = changeSet.Commit?.Sha ?? string.Empty,
                    ContentSha = changeSet.Content?.Sha ?? string.Empty,
                    HtmlUrl = changeSet.Commit?.Url ?? string.Empty,
                    Branch = request.Branch
                };
            }
            catch (NotFoundException ex) { throw MapError(ex, $"Repository {request.Owner}/{request.Repo} not found."); }
            catch (Exception ex) { throw MapError(ex); }
        }

        /// <summary>
        /// Create-or-update a <strong>binary</strong> file (e.g. a release ZIP) via the
        /// GitHub Contents API. The raw bytes are supplied base64-encoded; the method
        /// decodes them locally first (so it can validate size and base64 integrity) and
        /// then re-encodes before sending to GitHub. This is necessary because
        /// <see cref="UpsertFileAsync(GitHubGateway.Application.DTOs.UpdateFileRequest, CancellationToken)"/>
        /// treats <c>Content</c> as UTF-8 text — which would corrupt binary payloads.
        /// </summary>
        /// <remarks>
        /// Internally the method picks <c>CreateFile</c> vs <c>UpdateFile</c> based on whether
        /// the file already exists (looked up by GET /repos/.../contents/{path}). This split
        /// is required because Octokit v14's <c>UpdateFileRequest</c> constructor rejects
        /// both <c>null</c> and empty-string for the <c>sha</c> parameter, so we cannot pass
        /// "no sha" through that path for new files.
        /// </remarks>
        public async Task<CommitResultDto> UpsertBinaryFileAsync(GitHubGateway.Application.DTOs.UpdateBinaryFileRequest request, CancellationToken ct = default)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Path)) throw new ArgumentException("Path is required.", nameof(request.Path));
            if (string.IsNullOrWhiteSpace(request.ContentBase64)) throw new ArgumentException("ContentBase64 is required.", nameof(request.ContentBase64));

            try
            {
                // Decode the base64 (cleaning whitespace that some encoders add).
                var cleaned = request.ContentBase64.Replace("\n", "").Replace("\r", "").Replace(" ", "");
                byte[] raw;
                try
                {
                    raw = Convert.FromBase64String(cleaned);
                }
                catch (FormatException ex)
                {
                    throw new ArgumentException($"ContentBase64 is not valid base64: {ex.Message}", ex);
                }

                // GitHub accepts files up to 100 MB. We cap at 80 MB raw to leave room
                // for base64 expansion (~33%) and JSON overhead.
                if (raw.Length > 80L * 1024 * 1024)
                    throw new ArgumentException($"File too large: {raw.Length:N0} bytes (max 80 MB).");

                // Re-encode for GitHub Contents API.
                var base64 = Convert.ToBase64String(raw);

                // Decide create vs update:
                //  - If caller supplied a sha → update via UpdateFile (with that sha)
                //  - If caller didn't supply a sha → look up the file
                //    - If found → update via UpdateFile with the looked-up sha
                //    - If not found (404) → create via CreateFile (no sha needed)
                string? sha = request.Sha;
                bool useCreate = false;
                if (string.IsNullOrEmpty(sha))
                {
                    try
                    {
                        var existing = await _octokit.Repository.Content.GetAllContentsByRef(
                            request.Owner, request.Repo, request.Path, request.Branch);
                        sha = existing.Count > 0 ? existing[0].Sha : null;
                        useCreate = string.IsNullOrEmpty(sha);
                    }
                    catch (NotFoundException)
                    {
                        // File doesn't exist yet → use CreateFile
                        useCreate = true;
                    }
                }

                Octokit.RepositoryContentChangeSet changeSet;
                if (useCreate)
                {
                    // CreateFileRequest(message, content, branch, convertContentToBase64)
                    // convertContentToBase64=false → content is already base64
                    var createReq = new CreateFileRequest(
                        request.CommitMessage, base64, request.Branch, convertContentToBase64: false);
                    changeSet = await _octokit.Repository.Content.CreateFile(
                        request.Owner, request.Repo, request.Path, createReq);
                }
                else
                {
                    var updateReq = new OctokitUpdateFileRequest(
                        request.CommitMessage, base64, sha ?? string.Empty, request.Branch);
                    changeSet = await _octokit.Repository.Content.UpdateFile(
                        request.Owner, request.Repo, request.Path, updateReq);
                }

                Invalidate(request.Owner, request.Repo, request.Branch, request.Path);
                return new CommitResultDto
                {
                    CommitSha = changeSet.Commit?.Sha ?? string.Empty,
                    ContentSha = changeSet.Content?.Sha ?? string.Empty,
                    HtmlUrl = changeSet.Commit?.Url ?? string.Empty,
                    Branch = request.Branch
                };
            }
            catch (NotFoundException ex) { throw MapError(ex, $"Repository {request.Owner}/{request.Repo} not found."); }
            catch (Exception ex) { throw MapError(ex); }
        }

        public async Task<CommitResultDto> DeleteFileAsync(GitHubGateway.Application.DTOs.DeleteFileRequest request, CancellationToken ct = default)
        {
            try
            {
                await _octokit.Repository.Content.DeleteFile(
                    request.Owner, request.Repo, request.Path,
                    new OctokitDeleteFileRequest(request.CommitMessage, request.Sha, request.Branch));

                Invalidate(request.Owner, request.Repo, request.Branch, request.Path);
                return new CommitResultDto
                {
                    CommitSha = string.Empty,    // Octokit v14 DeleteFile returns void
                    ContentSha = request.Sha,
                    HtmlUrl = string.Empty,
                    Branch = request.Branch
                };
            }
            catch (NotFoundException ex) { throw MapError(ex, "File or repo not found."); }
            catch (Exception ex) { throw MapError(ex); }
        }

        public async Task<BranchDto> CreateBranchAsync(CreateBranchRequest request, CancellationToken ct = default)
        {
            try
            {
                var sourceRef = await _octokit.Git.Reference.Get(request.Owner, request.Repo, $"heads/{request.FromBranch}");
                var newRef = await _octokit.Git.Reference.Create(request.Owner, request.Repo,
                    new NewReference($"refs/heads/{request.NewBranchName}", sourceRef.Object.Sha));

                _cache.Remove($"branches:{request.Owner}/{request.Repo}");
                return new BranchDto
                {
                    Name = request.NewBranchName,
                    CommitSha = newRef.Object.Sha,
                    Protected = false
                };
            }
            catch (NotFoundException ex) { throw MapError(ex, $"Source branch '{request.FromBranch}' not found."); }
            catch (Exception ex) { throw MapError(ex); }
        }

        public async Task<PullRequestDto> CreatePullRequestAsync(CreatePullRequestRequest request, CancellationToken ct = default)
        {
            try
            {
                var pr = new NewPullRequest(request.Title, request.Head, request.Base)
                {
                    Body = request.Body,
                    Draft = request.Draft
                };
                var created = await _octokit.PullRequest.Create(request.Owner, request.Repo, pr);
                return new PullRequestDto
                {
                    Number = created.Number,
                    Title = created.Title,
                    State = created.State.StringValue,
                    HtmlUrl = created.HtmlUrl,
                    Head = created.Head?.Ref ?? request.Head,
                    Base = created.Base?.Ref ?? request.Base,
                    Draft = created.Draft
                };
            }
            catch (NotFoundException ex) { throw MapError(ex, $"Branch {request.Head} or {request.Base} not found."); }
            catch (Exception ex) { throw MapError(ex); }
        }

        private void Invalidate(string owner, string repo, string branch, string path)
        {
            _cache.Remove($"branches:{owner}/{repo}");
            _cache.Remove($"contents:{owner}/{repo}:{branch}:");
            _cache.Remove($"contents:{owner}/{repo}:{branch}:{path}");
            _cache.Remove($"file:{owner}/{repo}:{branch}:{path}");
        }

        private static GitHubGatewayException MapError(Exception ex, string? friendly = null)
        {
            if (ex is NotFoundException)
                return new GitHubGatewayException(friendly ?? ex.Message, HttpStatusCode.NotFound);
            if (ex is AuthorizationException)
                return new GitHubGatewayException("GitHub authorization failed. Check the PAT.", HttpStatusCode.Unauthorized);
            if (ex is ForbiddenException)
                return new GitHubGatewayException("GitHub forbidden (rate limit or insufficient scope).", HttpStatusCode.Forbidden);
            if (ex is ApiValidationException)
                return new GitHubGatewayException("GitHub validation error: " + ex.Message, HttpStatusCode.BadRequest);
            return new GitHubGatewayException(friendly ?? ex.Message, HttpStatusCode.BadGateway);
        }
    }

    public sealed class GitHubGatewayException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public GitHubGatewayException(string message, HttpStatusCode statusCode) : base(message)
        {
            StatusCode = statusCode;
        }
        public GitHubGatewayException(string message, HttpStatusCode statusCode, Exception inner)
            : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }
}
