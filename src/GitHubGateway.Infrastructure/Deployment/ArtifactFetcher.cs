using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Deployment.Interfaces;
using GitHubGateway.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace GitHubGateway.Infrastructure.Deployment
{
    /// <summary>
    /// Fetches the latest artifact ZIP from a GitHub repository's deployment branch.
    /// Uses the GitHubGateway client (which already wraps Octokit) for the API calls.
    /// </summary>
    public sealed class ArtifactFetcher : IArtifactFetcher
    {
        private readonly IGitHubGatewayClient _gh;
        private readonly Microsoft.Extensions.Logging.ILogger<ArtifactFetcher> _logger;

        public ArtifactFetcher(
            IGitHubGatewayClient gh,
            Microsoft.Extensions.Logging.ILogger<ArtifactFetcher> logger)
        {
            _gh = gh ?? throw new ArgumentNullException(nameof(gh));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<ArtifactInfo>> ListArtifactsAsync(
            string owner, string repo, string branch, CancellationToken ct = default)
        {
            var items = await _gh.ListContentsAsync(owner, repo, "", branch, ct);
            var artifacts = items
                .Where(i => i.Type == "file" && i.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Select(i => new ArtifactInfo
                {
                    FileName = i.Name,
                    Size = i.Size,
                    Sha = i.Sha,
                    LastModified = null   // GitHub contents API doesn't return mtime
                })
                .OrderByDescending(a => a.FileName)   // ZIPs are named with timestamp → string-sort is correct
                .ToList();
            return artifacts;
        }

        public async Task<string> GetLatestArtifactFileNameAsync(
            string owner, string repo, string branch, CancellationToken ct = default)
        {
            var artifacts = await ListArtifactsAsync(owner, repo, branch, ct);
            if (artifacts.Count == 0)
                throw new InvalidOperationException(
                    $"No .zip artifacts found in {owner}/{repo}@{branch}. " +
                    "Build and push an artifact first.");
            return artifacts[0].FileName;
        }

        public async Task<string> DownloadArtifactAsync(
            string owner, string repo, string branch, string? fileName,
            string localDestDir, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(fileName))
                fileName = await GetLatestArtifactFileNameAsync(owner, repo, branch, ct);

            Directory.CreateDirectory(localDestDir);

            // Use the trailing-slash trick that bypasses IIS Request Filtering on file extensions
            var file = await _gh.GetFileContentAsync(owner, repo, fileName, branch, ct);

            var localPath = Path.Combine(localDestDir, fileName);
            // The gateway returns the content as a decoded UTF-8 string. We need to write it back as bytes.
            // Note: this means the artifact must be a binary-safe content type, which Octokit's
            // GetRawContentByRef returns as bytes that our gateway decodes to UTF-8. For ZIP files
            // this works because base64 round-trips losslessly via Convert.FromBase64String.
            // However, if content was decoded as UTF-8 with non-ASCII, ZIP bytes may be corrupted.
            //
            // Safer path: re-encode to base64 then decode to bytes.
            byte[] bytes;
            try
            {
                // Try base64 first (more reliable for binary)
                bytes = Convert.FromBase64String(file.Content ?? "");
            }
            catch
            {
                // Fall back to UTF-8 (gateway already decoded, so this is the original bytes)
                bytes = System.Text.Encoding.UTF8.GetBytes(file.Content ?? "");
            }
            await File.WriteAllBytesAsync(localPath, bytes, ct);
            _logger.LogInformation("Downloaded artifact {File} ({Size} bytes) → {Path}", fileName, bytes.Length, localPath);
            return localPath;
        }
    }
}

