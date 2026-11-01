using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using GitHubGateway.Application.Deployment.DTOs;
using GitHubGateway.Application.Deployment.Interfaces;
using IGitHubFtpClient = GitHubGateway.Application.Deployment.Interfaces.IFtpClient;

namespace GitHubGateway.Infrastructure.Ftp
{
    /// <summary>
    /// FTP/FTPS implementation of <see cref="IGitHubFtpClient"/> using FluentFTP v55.
    /// For SFTP, see <see cref="SftpClientImpl"/>.
    /// </summary>
    public sealed class FluentFtpClientImpl : IGitHubFtpClient, IDisposable
    {
        private readonly FtpConnectionConfig _config;
        private readonly AsyncFtpClient _client;

        public FluentFtpClientImpl(FtpConnectionConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            var password = ResolvePassword(_config);
            _client = new AsyncFtpClient(_config.Host, _config.Username, password, _config.Port);
            _client.Config.DataConnectionType = _config.PassiveMode ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
            _client.Config.ConnectTimeout = _config.TimeoutSeconds * 1000;
            _client.Config.ReadTimeout = _config.TimeoutSeconds * 1000;
            _client.Config.DataConnectionConnectTimeout = _config.TimeoutSeconds * 1000;
            _client.Config.DataConnectionReadTimeout = _config.TimeoutSeconds * 1000;
            _client.Config.ValidateAnyCertificate = true;
            _client.Config.EncryptionMode = ParseEncryptionMode(_config.EncryptionMode);
            _client.Config.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
        }

        public bool IsConnected => _client.IsConnected;

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await _client.Connect(ct);
        }

        public async Task<IReadOnlyList<RemoteTreeNode>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            var items = await _client.GetListing(remotePath, FtpListOption.ForceList, ct);
            return items.Select(MapItem).ToList();
        }

        public async Task<RemoteTreeNode> GetTreeAsync(string remotePath, int maxDepth, CancellationToken ct = default)
        {
            var items = await _client.GetListing(remotePath, maxDepth > 0 ? FtpListOption.Recursive : FtpListOption.ForceList, ct);
            return BuildTree(remotePath, items, maxDepth);
        }

        public async Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            await _client.CreateDirectory(remotePath, true, ct);
        }

        public async Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default)
        {
            var progressAdapter = progress != null
                ? new Progress<FtpProgress>(p => progress.Report(p.TransferredBytes))
                : null;
            await _client.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, false, FtpVerify.None, progressAdapter, ct);
        }

        public async Task RenameAsync(string remotePath, string newPath, CancellationToken ct = default)
        {
            await _client.Rename(remotePath, newPath, ct);
        }

        public async Task DeleteFileAsync(string remotePath, CancellationToken ct = default)
        {
            await _client.DeleteFile(remotePath, ct);
        }

        public async Task DeleteDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            await _client.DeleteDirectory(remotePath, FtpListOption.Recursive, ct);
        }

        public async Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default)
        {
            return await _client.FileExists(remotePath, ct) || await _client.DirectoryExists(remotePath, ct);
        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            if (_client.IsConnected)
                await _client.Disconnect(ct);
        }

        public void Dispose()
        {
            try { _client?.Dispose(); } catch { /* ignore */ }
        }

        // ---------------------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------------------

        private static string ResolvePassword(FtpConnectionConfig config)
        {
            if (!string.IsNullOrEmpty(config.Password)) return config.Password;
            if (!string.IsNullOrEmpty(config.PasswordEnvVar))
                return Environment.GetEnvironmentVariable(config.PasswordEnvVar) ?? string.Empty;
            return string.Empty;
        }

        private static FtpEncryptionMode ParseEncryptionMode(string? s)
        {
            if (string.IsNullOrEmpty(s)) return FtpEncryptionMode.Auto;
            return s.ToLowerInvariant() switch
            {
                "none"      => FtpEncryptionMode.None,
                "implicit"  => FtpEncryptionMode.Implicit,
                "explicit"   => FtpEncryptionMode.Explicit,
                "explicittls" => FtpEncryptionMode.Explicit,
                "explicitssl" => FtpEncryptionMode.Explicit,
                "auto"      => FtpEncryptionMode.Auto,
                _           => FtpEncryptionMode.Auto
            };
        }

        private static RemoteTreeNode MapItem(FtpListItem item)
        {
            return new RemoteTreeNode
            {
                Name = item.Name,
                Path = item.FullName,
                IsDirectory = item.Type == FtpObjectType.Directory,
                Size = item.Size,
                Modified = item.Modified
            };
        }

        private static RemoteTreeNode BuildTree(string rootPath, FtpListItem[] items, int maxDepth)
        {
            var root = new RemoteTreeNode
            {
                Name = Path.GetFileName(rootPath.TrimEnd('/')) ?? rootPath,
                Path = rootPath,
                IsDirectory = true,
                Children = new List<RemoteTreeNode>()
            };

            var lookup = new Dictionary<string, RemoteTreeNode>(StringComparer.OrdinalIgnoreCase)
            {
                [Normalize(rootPath)] = root
            };

            foreach (var item in items)
            {
                var node = MapItem(item);
                node.Children = new List<RemoteTreeNode>();
                var normalized = Normalize(item.FullName ?? "");
                if (!lookup.ContainsKey(normalized)) lookup[normalized] = node;

                // Find parent
                var parentPath = Normalize(Path.GetDirectoryName(item.FullName?.TrimEnd('/')) ?? "/");
                if (!lookup.TryGetValue(parentPath, out var parent))
                    parent = root;
                parent.Children.Add(node);
            }

            if (maxDepth > 0) PruneDepth(root, 0, maxDepth);
            return root;
        }

        private static void PruneDepth(RemoteTreeNode node, int currentDepth, int maxDepth)
        {
            if (currentDepth >= maxDepth)
            {
                node.Children.Clear();
                return;
            }
            foreach (var c in node.Children) PruneDepth(c, currentDepth + 1, maxDepth);
        }

        private static string Normalize(string? path)
        {
            return (path ?? "").Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }
    }
}
