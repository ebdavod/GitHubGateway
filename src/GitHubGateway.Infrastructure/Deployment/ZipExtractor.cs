using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Deployment;
using GitHubGateway.Application.Deployment.Interfaces;

namespace GitHubGateway.Infrastructure.Deployment
{
    /// <summary>
    /// Default ZIP extractor using System.IO.Compression.
    /// Supports:
    ///   - Excluding files based on glob patterns (matched against the entry's full path in the archive)
    ///   - Stripping the top-level folder (common when GitHub ZIPs include a {repo}-{sha}/ prefix)
    /// </summary>
    public sealed class ZipExtractor : IZipExtractor
    {
        public async Task<IReadOnlyList<string>> ExtractAsync(string zipPath, string destDir,
            string[] excludePatterns, bool stripRootFolder, CancellationToken ct = default)
        {
            if (!File.Exists(zipPath)) throw new FileNotFoundException("ZIP file not found", zipPath);
            Directory.CreateDirectory(destDir);

            var extracted = new List<string>();
            using var archive = ZipFile.OpenRead(zipPath);

            // Determine the common root folder if stripRootFolder is true.
            string? rootPrefix = null;
            if (stripRootFolder)
            {
                rootPrefix = FindCommonRoot(archive.Entries);
            }

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue;  // directory entry

                var relativePath = entry.FullName.Replace('\\', '/');
                if (rootPrefix != null && relativePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(rootPrefix.Length).TrimStart('/');

                if (FilePatternMatcher.MatchesAny(relativePath, excludePatterns)) continue;

                var destPath = Path.Combine(destDir, relativePath);
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                entry.ExtractToFile(destPath, overwrite: true);
                extracted.Add(relativePath);
            }

            return await Task.FromResult(extracted);
        }

        /// <summary>
        /// Returns the common top-level folder prefix across all entries.
        /// E.g. if all entries start with "avalonline-api-20260101/", returns "avalonline-api-20260101/".
        /// Returns null if entries are at the root (no common prefix).
        /// </summary>
        private static string? FindCommonRoot(System.Collections.ObjectModel.ReadOnlyCollection<ZipArchiveEntry> entries)
        {
            if (entries.Count == 0) return null;
            string? candidate = null;
            foreach (var entry in entries)
            {
                var fullName = entry.FullName.Replace('\\', '/');
                var slash = fullName.IndexOf('/');
                if (slash <= 0) return null;  // root-level entry → no common prefix
                var prefix = fullName.Substring(0, slash + 1);
                if (candidate == null) candidate = prefix;
                else if (!string.Equals(candidate, prefix, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return candidate;
        }
    }
}
