using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Deployment.DTOs;

namespace GitHubGateway.Application.Deployment.Interfaces
{
    /// <summary>
    /// Low-level port for FTP/SFTP operations. Implementations are provided by the
    /// Infrastructure layer (FluentFTP for FTP/FTPS, SSH.NET for SFTP).
    /// </summary>
    public interface IFtpClient : System.IDisposable
    {
        /// <summary>Establishes a connection to the FTP server.</summary>
        Task ConnectAsync(CancellationToken ct = default);

        /// <summary>Returns true if currently connected.</summary>
        bool IsConnected { get; }

        /// <summary>Lists the contents of a remote directory.</summary>
        Task<IReadOnlyList<RemoteTreeNode>> ListDirectoryAsync(string remotePath, CancellationToken ct = default);

        /// <summary>Recursively lists a directory tree, up to the given max depth (0 = direct children only).</summary>
        Task<RemoteTreeNode> GetTreeAsync(string remotePath, int maxDepth, CancellationToken ct = default);

        /// <summary>Creates a remote directory (and any missing parents).</summary>
        Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default);

        /// <summary>Uploads a single local file to a remote path.</summary>
        Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default);

        /// <summary>Renames a remote file or directory.</summary>
        Task RenameAsync(string remotePath, string newPath, CancellationToken ct = default);

        /// <summary>Deletes a remote file. Throws if the path is a directory.</summary>
        Task DeleteFileAsync(string remotePath, CancellationToken ct = default);

        /// <summary>Deletes a remote directory (must be empty).</summary>
        Task DeleteDirectoryAsync(string remotePath, CancellationToken ct = default);

        /// <summary>Returns true if the given remote path exists (file or directory).</summary>
        Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default);

        /// <summary>Disconnects gracefully.</summary>
        Task DisconnectAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Factory that creates an IFtpClient from a FtpConnectionConfig.
    /// Selects FTP/FTPS or SFTP based on the Mode field.
    /// </summary>
    public interface IFtpClientFactory
    {
        IFtpClient Create(FtpConnectionConfig config);
    }

    /// <summary>
    /// Fetches the latest artifact ZIP from the deployment branch of a GitHub repository.
    /// </summary>
    public interface IArtifactFetcher
    {
        /// <summary>Returns the file name of the latest ZIP artifact (without downloading).</summary>
        Task<string> GetLatestArtifactFileNameAsync(string owner, string repo, string branch, CancellationToken ct = default);

        /// <summary>Downloads the latest (or specific) artifact ZIP to a local path. Returns local file path.</summary>
        Task<string> DownloadArtifactAsync(string owner, string repo, string branch, string? fileName, string localDestDir, CancellationToken ct = default);

        /// <summary>Lists all artifacts in the deployment branch.</summary>
        Task<IReadOnlyList<ArtifactInfo>> ListArtifactsAsync(string owner, string repo, string branch, CancellationToken ct = default);
    }

    public sealed class ArtifactInfo
    {
        public string FileName { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha { get; set; } = string.Empty;
        public DateTimeOffset? LastModified { get; set; }
    }

    /// <summary>
    /// Extracts a ZIP file to a directory with optional pattern-based exclusions.
    /// </summary>
    public interface IZipExtractor
    {
        /// <summary>Extracts the ZIP to the destination directory. Returns the list of extracted files (relative paths).</summary>
        Task<IReadOnlyList<string>> ExtractAsync(string zipPath, string destDir, string[] excludePatterns, bool stripRootFolder, CancellationToken ct = default);
    }

    /// <summary>
    /// Orchestrates a complete deployment: fetch artifact → connect → backup → extract → upload → cleanup.
    /// </summary>
    public interface IDeploymentOrchestrator
    {
        /// <summary>Triggers a deployment job. Returns the JobId immediately (does not wait).</summary>
        Task<DeployJobResponse> TriggerAsync(string targetName, TriggerDeployRequest? request, CancellationToken ct = default);

        /// <summary>Returns the live status of a job, or the latest job if jobId is null.</summary>
        Task<DeployStatusResponse?> GetStatusAsync(string targetName, string? jobId, CancellationToken ct = default);

        /// <summary>Returns the history of past deployments for the target.</summary>
        Task<IReadOnlyList<DeployResultDto>> GetHistoryAsync(string targetName, int maxCount, CancellationToken ct = default);

        /// <summary>Returns true if the target has a deployment currently running.</summary>
        bool IsRunning(string targetName);
    }

    /// <summary>
    /// In-memory store for live deployment status (per target).
    /// </summary>
    public interface IDeploymentStatusStore
    {
        DeployStatusResponse? Get(string targetName, string? jobId);
        void Update(string targetName, DeployStatusResponse status);
        void Clear(string targetName);
    }

    /// <summary>
    /// Persistent store for deployment history (one record per completed job).
    /// </summary>
    public interface IDeploymentHistoryStore
    {
        Task<IReadOnlyList<DeployResultDto>> GetAsync(string targetName, int maxCount, CancellationToken ct = default);
        Task AddAsync(DeployResultDto result, CancellationToken ct = default);
        Task<int> PruneAsync(string targetName, int keepCount, CancellationToken ct = default);
    }
}
