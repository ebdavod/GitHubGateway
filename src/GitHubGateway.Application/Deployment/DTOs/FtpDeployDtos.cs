using System;
using System.Collections.Generic;

namespace GitHubGateway.Application.Deployment.DTOs
{
    // ============================================================================
    //  Configuration DTOs (mapped from appsettings.json → PftpDeployOptions)
    // ============================================================================

    /// <summary>
    /// Connection settings for a single FTP/SFTP target.
    /// </summary>
    public sealed class FtpConnectionConfig
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 21;
        /// <summary>"Ftp" | "Ftps" | "Sftp"</summary>
        public string Mode { get; set; } = "Ftp";
        public string Username { get; set; } = string.Empty;
        /// <summary>Read from env var if empty. Env pattern: FtpDeploy__Targets__{n}__Ftp__Password</summary>
        public string Password { get; set; } = string.Empty;
        /// <summary>Env var name that overrides Password at runtime.</summary>
        public string PasswordEnvVar { get; set; } = string.Empty;
        public string RemoteBasePath { get; set; } = "/";
        public bool PassiveMode { get; set; } = true;
        /// <summary>For FTPS — None | Implicit | ExplicitTls | ExplicitSsl</summary>
        public string EncryptionMode { get; set; } = "ExplicitTls";
        public int TimeoutSeconds { get; set; } = 30;
    }

    /// <summary>
    /// Deployment-specific options per target.
    /// </summary>
    public sealed class DeployOptions
    {
        public bool CreateRemoteBackup { get; set; } = true;
        public string BackupSuffix { get; set; } = "bak";
        /// <summary>Files matching these patterns are excluded from upload.</summary>
        public string[] UploadExcludePatterns { get; set; } = Array.Empty<string>();
        /// <summary>Files matching these patterns are not renamed to .bak (kept as-is).</summary>
        public string[] BackupExcludePatterns { get; set; } = Array.Empty<string>();
        /// <summary>If true, the top-level folder inside the ZIP is stripped before upload.</summary>
        public bool StripRootFolder { get; set; } = true;
        /// <summary>If true, the contents of the ZIP overwrite existing remote files (otherwise delete-then-upload).</summary>
        public bool OverwriteExisting { get; set; } = true;
        /// <summary>Max parallel uploads. Defaults to 3.</summary>
        public int MaxParallelUploads { get; set; } = 3;
    }

    /// <summary>
    /// A configured deployment target.
    /// </summary>
    public sealed class FtpDeployTarget
    {
        public string Name { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Repo { get; set; } = string.Empty;
        public string Branch { get; set; } = "deployment";
        public bool Enabled { get; set; } = true;
        public FtpConnectionConfig Ftp { get; set; } = new();
        public DeployOptions Deploy { get; set; } = new();
    }

    // ============================================================================
    //  Operation DTOs (used in API requests/responses)
    // ============================================================================

    /// <summary>
    /// Response returned when a deployment is triggered. Contains the JobId used to poll status.
    /// </summary>
    public record DeployJobResponse
    {
        public string JobId { get; init; } = string.Empty;
        public string TargetName { get; init; } = string.Empty;
        public DateTimeOffset StartedAt { get; init; }
        public string StatusUrl { get; init; } = string.Empty;
    }

    /// <summary>
    /// Live status of a deployment job (or the last job if no jobId supplied).
    /// </summary>
    public record DeployStatusResponse
    {
        public string JobId { get; init; } = string.Empty;
        public string TargetName { get; init; } = string.Empty;
        public DeployState State { get; init; }
        public int ProgressPercent { get; init; }
        public int FilesUploaded { get; init; }
        public int FilesTotal { get; init; }
        public long BytesTransferred { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
        public string? CurrentFile { get; init; }
        public string? ArtifactFileName { get; init; }
        public string? Error { get; init; }
    }

    public enum DeployState
    {
        Idle = 0,
        Pending = 1,
        FetchingArtifact = 2,
        Connecting = 3,
        BackingUp = 4,
        Extracting = 5,
        Uploading = 6,
        CleaningUp = 7,
        Success = 10,
        Failed = 99
    }

    /// <summary>
    /// Final deployment result, persisted to the history store.
    /// </summary>
    public record DeployResultDto
    {
        public string JobId { get; init; } = string.Empty;
        public string TargetName { get; init; } = string.Empty;
        public string ArtifactFileName { get; init; } = string.Empty;
        public string ArtifactSha { get; init; } = string.Empty;
        public bool Success { get; init; }
        public int FilesUploaded { get; init; }
        public int FilesFailed { get; init; }
        public long TotalBytes { get; init; }
        public TimeSpan Duration { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset FinishedAt { get; init; }
        public string? ErrorMessage { get; init; }
        public List<FileTransferResultDto> Files { get; init; } = new();
    }

    public record FileTransferResultDto
    {
        public string LocalPath { get; init; } = string.Empty;
        public string RemotePath { get; init; } = string.Empty;
        public long Size { get; init; }
        public bool Success { get; init; }
        public string? Error { get; init; }
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Information about a remote file or directory on the FTP server.
    /// </summary>
    public class RemoteTreeNode
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTimeOffset Modified { get; set; }
        public List<RemoteTreeNode> Children { get; set; } = new();
    }

    /// <summary>
    /// Result of an FTP connection test.
    /// </summary>
    public record FtpTestResult
    {
        public bool Success { get; init; }
        public string Host { get; init; } = string.Empty;
        public int Port { get; init; }
        public string Mode { get; init; } = string.Empty;
        public string? ServerMessage { get; init; }
        public DateTimeOffset TestedAt { get; init; } = DateTimeOffset.UtcNow;
        public string? Error { get; init; }
        public RemoteTreeNode? RootListing { get; init; }
    }

    /// <summary>
    /// Public-facing target info (without credentials).
    /// </summary>
    public record TargetInfoDto
    {
        public string Name { get; init; } = string.Empty;
        public string Owner { get; init; } = string.Empty;
        public string Repo { get; init; } = string.Empty;
        public string Branch { get; init; } = "deployment";
        public bool Enabled { get; init; }
        public string Host { get; init; } = string.Empty;
        public int Port { get; init; }
        public string Mode { get; init; } = string.Empty;
        public string RemoteBasePath { get; init; } = string.Empty;
        public bool CreateRemoteBackup { get; init; }
        public bool StripRootFolder { get; init; }
        public string[] UploadExcludePatterns { get; init; } = Array.Empty<string>();
        public bool HasPassword { get; init; }
        public bool IsCurrentlyRunning { get; init; }
        public DeployStatusResponse? LastStatus { get; init; }
    }

    /// <summary>
    /// Request body for triggering a deployment (optional overrides).
    /// </summary>
    public record TriggerDeployRequest
    {
        /// <summary>If set, deploy this specific ZIP file name instead of the latest.</summary>
        public string? ArtifactFileName { get; init; }
        /// <summary>If true, skip the backup step even if config has it enabled.</summary>
        public bool SkipBackup { get; init; }
        /// <summary>If true, do a dry-run (list what would happen without uploading).</summary>
        public bool DryRun { get; init; }
    }
}
