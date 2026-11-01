using System;
using System.Collections.Generic;

namespace GitHubGateway.Application.DTOs
{
    /// <summary>
    /// Represents a file content response from a GitHub repository.
    /// </summary>
    public record FileContentDto
    {
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Sha { get; init; } = string.Empty;
        public long Size { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public string Encoding { get; init; } = string.Empty;
        public string HtmlUrl { get; init; } = string.Empty;
        public string DownloadUrl { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents a directory entry (file or sub-directory) inside a GitHub repository.
    /// </summary>
    public record RepoItemDto
    {
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;  // "file" | "dir" | "symlink" | "submodule"
        public long Size { get; init; }
        public string Sha { get; init; } = string.Empty;
        public string HtmlUrl { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents a branch reference inside a GitHub repository.
    /// </summary>
    public record BranchDto
    {
        public string Name { get; init; } = string.Empty;
        public string CommitSha { get; init; } = string.Empty;
        public bool Protected { get; init; }
    }

    /// <summary>
    /// Represents a commit summary inside a GitHub repository.
    /// </summary>
    public record CommitDto
    {
        public string Sha { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Author { get; init; } = string.Empty;
        public DateTimeOffset Date { get; init; }
        public string HtmlUrl { get; init; } = string.Empty;
    }

    /// <summary>
    /// Request payload used to create or update a file in a GitHub repository.
    /// </summary>
    public record UpdateFileRequest
    {
        public string Owner { get; init; } = string.Empty;
        public string Repo { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Branch { get; init; } = "main";
        public string Content { get; init; } = string.Empty;
        public string CommitMessage { get; init; } = string.Empty;
        public string? Sha { get; init; }   // Required for updates; null for new files
    }

    /// <summary>
    /// Request payload used to create or update a <strong>binary</strong> file
    /// (e.g. a release ZIP). The bytes are sent as base64 inside the request body;
    /// the gateway base64-encodes them again before calling the GitHub Contents API.
    /// </summary>
    /// <remarks>
    /// Use this together with the <c>POST /api/v1/repos/{owner}/{repo}/artifacts/upload</c>
    /// endpoint, which routes around IIS Request Filtering that blocks the standard
    /// <c>PUT /api/v1/repos/{owner}/{repo}/contents/{path}</c> endpoint for any URL
    /// that contains a dot (<c>.</c>).
    /// </remarks>
    public record UpdateBinaryFileRequest
    {
        public string Owner { get; init; } = string.Empty;
        public string Repo { get; init; } = string.Empty;
        /// <summary>Full path in the repo, including extension (e.g. <c>avalonline-api-20261101-210935-6509c5a9.zip</c>).</summary>
        public string Path { get; init; } = string.Empty;
        public string Branch { get; init; } = "main";
        /// <summary>Base64-encoded raw bytes. The gateway decodes then re-encodes for GitHub.</summary>
        public string ContentBase64 { get; init; } = string.Empty;
        public string CommitMessage { get; init; } = string.Empty;
        /// <summary>Required for updates; null for new files.</summary>
        public string? Sha { get; init; }
    }

    /// <summary>
    /// Request payload used to delete a file from a GitHub repository.
    /// </summary>
    public record DeleteFileRequest
    {
        public string Owner { get; init; } = string.Empty;
        public string Repo { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Branch { get; init; } = "main";
        public string Sha { get; init; } = string.Empty;
        public string CommitMessage { get; init; } = string.Empty;
    }

    /// <summary>
    /// Request payload used to create a new branch from an existing source branch.
    /// </summary>
    public record CreateBranchRequest
    {
        public string Owner { get; init; } = string.Empty;
        public string Repo { get; init; } = string.Empty;
        public string NewBranchName { get; init; } = string.Empty;
        public string FromBranch { get; init; } = "main";
    }

    /// <summary>
    /// Request payload used to create a Pull Request on a GitHub repository.
    /// </summary>
    public record CreatePullRequestRequest
    {
        public string Owner { get; init; } = string.Empty;
        public string Repo { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Head { get; init; } = string.Empty;     // Source branch
        public string Base { get; init; } = string.Empty;     // Target branch
        public string Body { get; init; } = string.Empty;
        public bool Draft { get; init; }
    }

    /// <summary>
    /// Request payload used to apply a unified git patch to a GitHub repository.
    /// For each file mentioned in the patch, the service computes the new content and pushes it.
    /// </summary>
    public record ApplyPatchRequest
    {
        public string Owner { get; init; } = string.Empty;
        public string Repo { get; init; } = string.Empty;
        public string Branch { get; init; } = "main";
        public string PatchText { get; init; } = string.Empty;
        public string CommitMessage { get; init; } = string.Empty;
    }

    /// <summary>
    /// Response returned after a commit operation (create/update/delete).
    /// </summary>
    public record CommitResultDto
    {
        public string CommitSha { get; init; } = string.Empty;
        public string ContentSha { get; init; } = string.Empty;
        public string HtmlUrl { get; init; } = string.Empty;
        public string Branch { get; init; } = string.Empty;
    }

    /// <summary>
    /// Represents a Pull Request that has been created on GitHub.
    /// </summary>
    public record PullRequestDto
    {
        public int Number { get; init; }
        public string Title { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string HtmlUrl { get; init; } = string.Empty;
        public string Head { get; init; } = string.Empty;
        public string Base { get; init; } = string.Empty;
        public bool Draft { get; init; }
    }

    /// <summary>
    /// Represents the result of applying a patch: per-file outcomes.
    /// </summary>
    public record PatchResultDto
    {
        public string Branch { get; init; } = string.Empty;
        public string CommitSha { get; init; } = string.Empty;
        public IReadOnlyList<PatchFileResult> Files { get; init; } = Array.Empty<PatchFileResult>();
    }

    public record PatchFileResult
    {
        public string Path { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;  // "added" | "modified" | "removed"
        public string? CommitSha { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// Lightweight information about the connection to GitHub (used by the health/info endpoint).
    /// </summary>
    public record ConnectionInfo
    {
        public bool Authenticated { get; init; }
        public string? UserName { get; init; }
        public string? PlanName { get; init; }
        public int RateLimitRemaining { get; init; }
        public int RateLimitTotal { get; init; }
        public long RateLimitResetUnixSeconds { get; init; }
    }
}
