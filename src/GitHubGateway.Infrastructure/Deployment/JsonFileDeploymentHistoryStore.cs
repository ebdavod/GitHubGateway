using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Deployment.DTOs;
using GitHubGateway.Application.Deployment.Interfaces;
using GitHubGateway.Application.Deployment.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitHubGateway.Infrastructure.Deployment
{
    /// <summary>
    /// History store that tries to persist to a JSON-lines file, but falls back to in-memory
    /// storage if the file system is not writable (e.g. restricted IIS Application Pool identity).
    /// The fallback ensures the FTP deployment endpoints remain functional even on locked-down hosts.
    /// </summary>
    public sealed class JsonFileDeploymentHistoryStore : IDeploymentHistoryStore
    {
        private readonly ILogger<JsonFileDeploymentHistoryStore> _logger;
        private readonly SemaphoreSlim _sem = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

        // File-based state
        private readonly string? _historyDir;
        private readonly bool _fileStoreEnabled;

        // In-memory fallback (per-target list, newest first)
        private readonly ConcurrentDictionary<string, List<DeployResultDto>> _memoryStore = new(StringComparer.OrdinalIgnoreCase);

        public JsonFileDeploymentHistoryStore(IOptions<FtpDeployOptions> options, ILogger<JsonFileDeploymentHistoryStore> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var configured = options?.Value?.TempDir;
            var candidateDir = ResolveWritableHistoryDir(configured);

            try
            {
                Directory.CreateDirectory(candidateDir);
                // Verify writability by creating a probe file
                var probe = Path.Combine(candidateDir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                _historyDir = candidateDir;
                _fileStoreEnabled = true;
                _logger.LogInformation("Deployment history will be persisted to {Path}", _historyDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot write to {Path}; falling back to in-memory history store.", candidateDir);
                _historyDir = null;
                _fileStoreEnabled = false;
            }
        }

        public async Task<IReadOnlyList<DeployResultDto>> GetAsync(string targetName, int maxCount, CancellationToken ct = default)
        {
            if (_fileStoreEnabled && _historyDir != null)
            {
                var file = GetFile(targetName);
                if (!File.Exists(file)) return Array.Empty<DeployResultDto>();

                await _sem.WaitAsync(ct);
                try
                {
                    var lines = await File.ReadAllLinesAsync(file, ct);
                    var records = new List<DeployResultDto>();
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var rec = JsonSerializer.Deserialize<DeployResultDto>(line, _jsonOptions);
                            if (rec != null) records.Add(rec);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse history record");
                        }
                    }
                    return records.OrderByDescending(r => r.FinishedAt).Take(maxCount).ToList();
                }
                finally
                {
                    _sem.Release();
                }
            }
            else
            {
                // In-memory fallback
                if (_memoryStore.TryGetValue(targetName, out var list))
                    return list.Take(maxCount).ToList();
                return Array.Empty<DeployResultDto>();
            }
        }

        public async Task AddAsync(DeployResultDto result, CancellationToken ct = default)
        {
            if (_fileStoreEnabled && _historyDir != null)
            {
                var file = GetFile(result.TargetName);
                await _sem.WaitAsync(ct);
                try
                {
                    var json = JsonSerializer.Serialize(result, _jsonOptions) + Environment.NewLine;
                    await File.AppendAllTextAsync(file, json, ct);
                }
                finally
                {
                    _sem.Release();
                }
            }
            else
            {
                // In-memory fallback
                var list = _memoryStore.GetOrAdd(result.TargetName, _ => new List<DeployResultDto>());
                lock (list)
                {
                    list.Insert(0, result);
                    // Cap at 100 to prevent unbounded growth
                    if (list.Count > 100) list.RemoveAt(list.Count - 1);
                }
            }
        }

        public async Task<int> PruneAsync(string targetName, int keepCount, CancellationToken ct = default)
        {
            if (_fileStoreEnabled && _historyDir != null)
            {
                var records = await GetAsync(targetName, int.MaxValue, ct);
                if (records.Count <= keepCount) return 0;
                var keep = records.Take(keepCount).ToList();
                await _sem.WaitAsync(ct);
                try
                {
                    var file = GetFile(targetName);
                    File.Delete(file);
                    foreach (var r in keep)
                    {
                        var json = JsonSerializer.Serialize(r, _jsonOptions) + Environment.NewLine;
                        await File.AppendAllTextAsync(file, json, ct);
                    }
                    return records.Count - keepCount;
                }
                finally
                {
                    _sem.Release();
                }
            }
            else
            {
                // In-memory fallback
                if (_memoryStore.TryGetValue(targetName, out var list))
                {
                    lock (list)
                    {
                        var removed = Math.Max(0, list.Count - keepCount);
                        if (removed > 0) list.RemoveRange(keepCount, removed);
                        return removed;
                    }
                }
                return 0;
            }
        }

        private string GetFile(string targetName) => Path.Combine(_historyDir!, $"{SanitizeFileName(targetName)}.jsonl");

        private static string SanitizeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        /// <summary>
        /// Resolves the configured TempDir to a path that exists/writable on the current OS.
        /// On Windows, "/tmp/..." is treated as relative and resolved under the OS temp dir.
        /// On Linux/Mac, "/tmp/..." is used as-is.
        /// </summary>
        private static string ResolveWritableHistoryDir(string? configured)
        {
            string baseDir;
            if (string.IsNullOrWhiteSpace(configured))
            {
                baseDir = Path.Combine(Path.GetTempPath(), "github-gateway", "ftp-deploy");
            }
            else
            {
                bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows);
                if (isWindows && configured.StartsWith("/"))
                {
                    var relative = configured.TrimStart('/');
                    baseDir = Path.Combine(Path.GetTempPath(), "github-gateway", relative.Replace('/', '\\'));
                }
                else
                {
                    baseDir = configured;
                }
            }
            return Path.Combine(baseDir, "history");
        }
    }
}
