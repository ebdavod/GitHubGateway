using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Deployment.DTOs;
using GitHubGateway.Application.Deployment.Interfaces;
using IGitHubFtpClient = GitHubGateway.Application.Deployment.Interfaces.IFtpClient;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace GitHubGateway.Infrastructure.Ftp
{
    /// <summary>
    /// SFTP implementation of <see cref="IGitHubFtpClient"/> using SSH.NET.
    /// </summary>
    public sealed class SftpClientImpl : IGitHubFtpClient
    {
        private readonly FtpConnectionConfig _config;
        private readonly SftpClient _client;

        public SftpClientImpl(FtpConnectionConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            var password = ResolvePassword(config);
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(password))
                methods.Add(new PasswordAuthenticationMethod(config.Username, password));
            var sftpPort = config.Port == 21 ? 22 : config.Port;
            var connectionInfo = new ConnectionInfo(config.Host, sftpPort, config.Username, methods.ToArray())
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            _client = new SftpClient(connectionInfo);
        }

        public bool IsConnected => _client.IsConnected;

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await Task.Run(() => _client.Connect(), ct);
        }

        public async Task<IReadOnlyList<RemoteTreeNode>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                if (!_client.IsConnected) _client.Connect();
                var files = _client.ListDirectory(remotePath);
                return files.Select(MapItem).ToList();
            }, ct);
        }

        public async Task<RemoteTreeNode> GetTreeAsync(string remotePath, int maxDepth, CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                if (!_client.IsConnected) _client.Connect();
                var root = new RemoteTreeNode
                {
                    Name = Path.GetFileName(remotePath.TrimEnd('/')),
                    Path = remotePath,
                    IsDirectory = true,
                    Children = new List<RemoteTreeNode>(),
                    Modified = _client.GetLastWriteTime(remotePath)
                };
                if (maxDepth == 0) return root;
                PopulateTree(root, remotePath, 1, maxDepth, ct);
                return root;
            }, ct);
        }

        private void PopulateTree(RemoteTreeNode parent, string remotePath, int depth, int maxDepth, CancellationToken ct)
        {
            if (depth > maxDepth) return;
            foreach (var item in _client.ListDirectory(remotePath))
            {
                if (item.Name == "." || item.Name == "..") continue;
                var node = MapItem(item);
                node.Children = new List<RemoteTreeNode>();
                parent.Children.Add(node);
                if (item.IsDirectory)
                {
                    PopulateTree(node, item.FullName, depth + 1, maxDepth, ct);
                }
            }
        }

        public async Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            await Task.Run(() =>
            {
                if (!_client.IsConnected) _client.Connect();
                CreateDirectoryRecursive(remotePath);
            }, ct);
        }

        private void CreateDirectoryRecursive(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/" || _client.Exists(path)) return;
            var parent = Path.GetDirectoryName(path.TrimEnd('/'))?.Replace('\\', '/') ?? "/";
            if (!string.IsNullOrEmpty(parent) && parent != "/" && !_client.Exists(parent))
                CreateDirectoryRecursive(parent);
            try { _client.CreateDirectory(path); } catch { /* already exists */ }
        }

        public async Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default)
        {
            await Task.Run(() =>
            {
                if (!_client.IsConnected) _client.Connect();
                using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read);
                Action<ulong>? onUpload = progress != null ? new Action<ulong>(b => progress.Report((long)b)) : null;
                _client.UploadFile(fs, remotePath, true, onUpload);
            }, ct);
        }

        public async Task RenameAsync(string remotePath, string newPath, CancellationToken ct = default)
        {
            await Task.Run(() => _client.RenameFile(remotePath, newPath), ct);
        }

        public async Task DeleteFileAsync(string remotePath, CancellationToken ct = default)
        {
            await Task.Run(() => _client.DeleteFile(remotePath), ct);
        }

        public async Task DeleteDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            await Task.Run(() =>
            {
                foreach (var f in _client.ListDirectory(remotePath))
                {
                    if (f.Name == "." || f.Name == "..") continue;
                    var path = f.FullName;
                    if (f.IsDirectory) DeleteDirectoryAsync(path, ct).GetAwaiter().GetResult();
                    else _client.DeleteFile(path);
                }
                _client.DeleteDirectory(remotePath);
            }, ct);
        }

        public async Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                if (!_client.IsConnected) _client.Connect();
                return _client.Exists(remotePath);
            }, ct);
        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            await Task.Run(() =>
            {
                if (_client.IsConnected) _client.Disconnect();
            }, ct);
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

        private static RemoteTreeNode MapItem(ISftpFile file)
        {
            return new RemoteTreeNode
            {
                Name = file.Name,
                Path = file.FullName,
                IsDirectory = file.IsDirectory,
                Size = file.Length,
                Modified = file.LastWriteTime
            };
        }
    }
}
