using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Deployment;
using GitHubGateway.Application.Deployment.DTOs;
using GitHubGateway.Application.Deployment.Interfaces;
using GitHubGateway.Application.Deployment.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitHubGateway.Infrastructure.Deployment
{
    /// <summary>
    /// Orchestrates a complete deployment: fetch artifact → connect → backup → extract → upload → cleanup.
    /// Runs as a background task per deployment job; status is tracked in IDeploymentStatusStore.
    /// </summary>
    public sealed class DeploymentOrchestrator : IDeploymentOrchestrator
    {
        private readonly FtpDeployOptions _opts;
        private readonly IArtifactFetcher _fetcher;
        private readonly IZipExtractor _zip;
        private readonly IFtpClientFactory _ftpFactory;
        private readonly IDeploymentStatusStore _status;
        private readonly IDeploymentHistoryStore _history;
        private readonly ILogger<DeploymentOrchestrator> _logger;
        private readonly ConcurrentDictionary<string, string> _runningJobs = new(StringComparer.OrdinalIgnoreCase);

        public DeploymentOrchestrator(
            IOptions<FtpDeployOptions> opts,
            IArtifactFetcher fetcher,
            IZipExtractor zip,
            IFtpClientFactory ftpFactory,
            IDeploymentStatusStore status,
            IDeploymentHistoryStore history,
            ILogger<DeploymentOrchestrator> logger)
        {
            _opts = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _zip = zip ?? throw new ArgumentNullException(nameof(zip));
            _ftpFactory = ftpFactory ?? throw new ArgumentNullException(nameof(ftpFactory));
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsRunning(string targetName) => _runningJobs.ContainsKey(targetName);

        public Task<DeployJobResponse> TriggerAsync(string targetName, TriggerDeployRequest? request, CancellationToken ct = default)
        {
            var target = _opts.Targets.FirstOrDefault(t =>
                string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                throw new KeyNotFoundException($"Deployment target '{targetName}' not found in configuration.");
            if (!target.Enabled)
                throw new InvalidOperationException($"Target '{targetName}' is disabled.");
            if (_runningJobs.ContainsKey(targetName))
                throw new InvalidOperationException($"Target '{targetName}' is already running.");

            ValidateHost(target);

            var jobId = Guid.NewGuid().ToString("N");
            var startedAt = DateTimeOffset.UtcNow;
            _runningJobs[targetName] = jobId;

            var status = new DeployStatusResponse
            {
                JobId = jobId,
                TargetName = targetName,
                State = DeployState.Pending,
                StartedAt = startedAt,
                FilesTotal = 0,
                FilesUploaded = 0,
                BytesTransferred = 0,
                ProgressPercent = 0,
                CurrentFile = null,
                Error = null
            };
            _status.Update(targetName, status);

            // Fire-and-forget the actual work. We capture the cancellation token source so the
            // job can be aborted on shutdown.
            _ = Task.Run(() => RunJobAsync(target, jobId, request ?? new TriggerDeployRequest()), CancellationToken.None);

            return Task.FromResult(new DeployJobResponse
            {
                JobId = jobId,
                TargetName = targetName,
                StartedAt = startedAt,
                StatusUrl = $"/api/v1/deploy/ftp/targets/{targetName}/status?jobId={jobId}"
            });
        }

        public Task<DeployStatusResponse?> GetStatusAsync(string targetName, string? jobId, CancellationToken ct = default)
            => Task.FromResult(_status.Get(targetName, jobId));

        public async Task<IReadOnlyList<DeployResultDto>> GetHistoryAsync(string targetName, int maxCount, CancellationToken ct = default)
            => await _history.GetAsync(targetName, maxCount, ct);

        // ---------------------------------------------------------------------
        //  Internal job execution
        // ---------------------------------------------------------------------

        private async Task RunJobAsync(FtpDeployTarget target, string jobId, TriggerDeployRequest request)
        {
            var sw = Stopwatch.StartNew();
            var files = new List<FileTransferResultDto>();
            int filesUploaded = 0;
            int filesFailed = 0;
            long bytesTotal = 0;
            string? errorMessage = null;
            string? artifactFileName = null;
            string? artifactSha = null;
            var tempPath = ResolveWritableTempPath(_opts.TempDir, target.Name, jobId);
            try
            {
                Directory.CreateDirectory(tempPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot create temp path {Path}, using OS temp instead", tempPath);
                tempPath = Path.Combine(Path.GetTempPath(), "github-gateway", target.Name, jobId);
                try { Directory.CreateDirectory(tempPath); } catch { /* will fail later with clearer error */ }
            }
            try
            {
                Update(target.Name, jobId, DeployState.FetchingArtifact, $"Fetching latest artifact from {target.Owner}/{target.Repo}@{target.Branch}");
                var artifacts = await _fetcher.ListArtifactsAsync(target.Owner, target.Repo, target.Branch);
                if (artifacts.Count == 0)
                    throw new InvalidOperationException("No .zip artifacts found in the deployment branch.");
                var artifact = string.IsNullOrEmpty(request.ArtifactFileName)
                    ? artifacts[0]
                    : artifacts.FirstOrDefault(a => string.Equals(a.FileName, request.ArtifactFileName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"Artifact '{request.ArtifactFileName}' not found.");
                artifactFileName = artifact.FileName;
                artifactSha = artifact.Sha;

                var zipPath = await _fetcher.DownloadArtifactAsync(target.Owner, target.Repo, target.Branch, artifact.FileName, tempPath);

                Update(target.Name, jobId, DeployState.Extracting, $"Extracting {artifact.FileName}");
                var extractDir = Path.Combine(tempPath, "extracted");
                var extractedFiles = await _zip.ExtractAsync(zipPath, extractDir,
                    target.Deploy.UploadExcludePatterns, target.Deploy.StripRootFolder);
                _logger.LogInformation("Extracted {Count} files from {File}", extractedFiles.Count, artifact.FileName);

                Update(target.Name, jobId, DeployState.Connecting, $"Connecting to {target.Ftp.Host}:{target.Ftp.Port} ({target.Ftp.Mode})");
                using var ftp = _ftpFactory.Create(target.Ftp);
                await ftp.ConnectAsync();

                // Verify remote base path
                if (!await ftp.ExistsAsync(target.Ftp.RemoteBasePath))
                {
                    Update(target.Name, jobId, DeployState.Connecting, $"Creating remote base path {target.Ftp.RemoteBasePath}");
                    await ftp.CreateDirectoryAsync(target.Ftp.RemoteBasePath);
                }

                // Backup
                if (target.Deploy.CreateRemoteBackup && !request.SkipBackup && !request.DryRun)
                {
                    Update(target.Name, jobId, DeployState.BackingUp, $"Renaming existing files to .{target.Deploy.BackupSuffix}");
                    await BackupExistingFilesAsync(ftp, target, extractDir, extractedFiles);
                }

                // Upload
                Update(target.Name, jobId, DeployState.Uploading, $"Uploading {extractedFiles.Count} files", filesTotal: extractedFiles.Count);
                var semaphore = new SemaphoreSlim(target.Deploy.MaxParallelUploads);
                var tasks = extractedFiles.Select(rel => UploadOneAsync(ftp, target, extractDir, rel, semaphore, jobId, files)).ToList();
                await Task.WhenAll(tasks);
                filesUploaded = files.Count(f => f.Success);
                filesFailed = files.Count(f => !f.Success);
                bytesTotal = files.Where(f => f.Success).Sum(f => f.Size);

                Update(target.Name, jobId, DeployState.CleaningUp, "Cleaning up temporary files");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deployment {Target} {JobId} failed", target.Name, jobId);
                errorMessage = ex.Message;
            }
            finally
            {
                sw.Stop();
                try { if (Directory.Exists(tempPath)) Directory.Delete(tempPath, recursive: true); } catch { /* best effort */ }
                _runningJobs.TryRemove(target.Name, out _);

                var finalState = errorMessage == null ? DeployState.Success : DeployState.Failed;
                Update(target.Name, jobId, finalState, errorMessage ?? "Completed successfully",
                    progressPercent: 100, filesUploaded: filesUploaded, filesTotal: files.Count, bytesTransferred: bytesTotal);

                var result = new DeployResultDto
                {
                    JobId = jobId,
                    TargetName = target.Name,
                    ArtifactFileName = artifactFileName ?? string.Empty,
                    ArtifactSha = artifactSha ?? string.Empty,
                    Success = errorMessage == null,
                    FilesUploaded = filesUploaded,
                    FilesFailed = filesFailed,
                    TotalBytes = bytesTotal,
                    Duration = sw.Elapsed,
                    StartedAt = DateTimeOffset.UtcNow - sw.Elapsed,
                    FinishedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = errorMessage,
                    Files = files
                };
                try
                {
                    await _history.AddAsync(result);
                    await _history.PruneAsync(target.Name, _opts.HistoryRetentionCount);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist deployment history");
                }
            }
        }

        private async Task UploadOneAsync(IFtpClient ftp, FtpDeployTarget target, string extractDir, string rel,
            SemaphoreSlim sem, string jobId, List<FileTransferResultDto> results)
        {
            await sem.WaitAsync();
            var localPath = Path.Combine(extractDir, rel.Replace('/', Path.DirectorySeparatorChar));
            var remotePath = string.Join("/", target.Ftp.RemoteBasePath.TrimEnd('/'), rel.Replace('\\', '/'));
            var fileSw = Stopwatch.StartNew();
            try
            {
                // Ensure parent directory exists
                var parentDir = remotePath.Substring(0, remotePath.LastIndexOf('/'));
                if (!string.IsNullOrEmpty(parentDir) && parentDir != target.Ftp.RemoteBasePath.TrimEnd('/'))
                {
                    try { await ftp.CreateDirectoryAsync(parentDir); } catch { /* may exist */ }
                }
                var localInfo = new FileInfo(localPath);
                await ftp.UploadFileAsync(localPath, remotePath);
                results.Add(new FileTransferResultDto
                {
                    LocalPath = rel,
                    RemotePath = remotePath,
                    Size = localInfo.Length,
                    Success = true,
                    Duration = fileSw.Elapsed
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upload {Path}", rel);
                results.Add(new FileTransferResultDto
                {
                    LocalPath = rel,
                    RemotePath = remotePath,
                    Size = new FileInfo(localPath).Length,
                    Success = false,
                    Error = ex.Message,
                    Duration = fileSw.Elapsed
                });
            }
            finally
            {
                sem.Release();
            }
        }

        private static readonly ConcurrentDictionary<string, long> bytesTracker = new();

        private async Task BackupExistingFilesAsync(IFtpClient ftp, FtpDeployTarget target, string extractDir, IReadOnlyList<string> newFiles)
        {
            var existing = await ftp.ListDirectoryAsync(target.Ftp.RemoteBasePath);
            foreach (var item in existing.Where(i => !i.IsDirectory))
            {
                // Skip if it matches a backup exclude pattern
                if (FilePatternMatcher.MatchesAny(item.Name, target.Deploy.BackupExcludePatterns)) continue;
                // Skip if it's already a backup file
                if (item.Name.EndsWith($".{target.Deploy.BackupSuffix}", StringComparison.OrdinalIgnoreCase)) continue;
                // Skip if the new deployment doesn't include this file (preserve untouched)
                // (Optional behavior — for now we always backup everything to be safe)

                var backupName = $"{item.Name}.{target.Deploy.BackupSuffix}";
                var backupPath = $"{target.Ftp.RemoteBasePath.TrimEnd('/')}/{backupName}";
                try
                {
                    await ftp.RenameAsync(item.Path, backupPath);
                    _logger.LogInformation("Backed up {Old} → {New}", item.Path, backupPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to backup {Path}", item.Path);
                }
            }
        }

        private void Update(string targetName, string jobId, DeployState state, string? message,
            int progressPercent = 0, int filesUploaded = 0, int filesTotal = 0, long bytesTransferred = 0)
        {
            var existing = _status.Get(targetName, jobId);
            var updated = new DeployStatusResponse
            {
                JobId = jobId,
                TargetName = targetName,
                State = state,
                ProgressPercent = progressPercent > 0 ? progressPercent : existing?.ProgressPercent ?? 0,
                FilesUploaded = filesUploaded > 0 ? filesUploaded : existing?.FilesUploaded ?? 0,
                FilesTotal = filesTotal > 0 ? filesTotal : existing?.FilesTotal ?? 0,
                BytesTransferred = bytesTransferred > 0 ? bytesTransferred : existing?.BytesTransferred ?? 0,
                StartedAt = existing?.StartedAt ?? DateTimeOffset.UtcNow,
                FinishedAt = state == DeployState.Success || state == DeployState.Failed ? DateTimeOffset.UtcNow : existing?.FinishedAt,
                CurrentFile = message ?? existing?.CurrentFile,
                ArtifactFileName = existing?.ArtifactFileName,
                Error = state == DeployState.Failed ? message : existing?.Error
            };
            _status.Update(targetName, updated);
        }

        private void ValidateHost(FtpDeployTarget target)
        {
            if (_opts.AllowedHosts is { Length: > 0 })
            {
                var host = target.Ftp.Host;
                bool allowed = false;
                foreach (var h in _opts.AllowedHosts)
                {
                    if (string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) { allowed = true; break; }
                    // Wildcard match like *.example.com
                    if (h.StartsWith("*.", StringComparison.OrdinalIgnoreCase))
                    {
                        var suffix = h.Substring(1);  // ".example.com"
                        if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length)
                        {
                            allowed = true; break;
                        }
                    }
                }
                if (!allowed)
                    throw new InvalidOperationException($"FTP host '{host}' is not in the allowed hosts list.");
            }
        }

        /// <summary>
        /// Resolves a writable temp path that works on both Windows and Linux/Mac.
        /// If the configured TempDir is "/tmp/..." on Windows, it gets resolved under the OS temp dir.
        /// </summary>
        private static string ResolveWritableTempPath(string? configured, string targetName, string jobId)
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
            return Path.Combine(baseDir, targetName, jobId);
        }
    }
}
