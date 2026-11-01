using System;
using GitHubGateway.Application.Deployment.DTOs;
using GitHubGateway.Application.Deployment.Interfaces;

namespace GitHubGateway.Infrastructure.Ftp
{
    /// <summary>
    /// Factory that creates the right IFtpClient implementation (FTP/FTPS via FluentFTP, or SFTP via SSH.NET).
    /// </summary>
    public sealed class FtpClientFactory : IFtpClientFactory
    {
        public IFtpClient Create(FtpConnectionConfig config)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.Host))
                throw new ArgumentException("Ftp.Host is required", nameof(config));

            var mode = (config.Mode ?? "Ftp").Trim().ToLowerInvariant();
            return mode switch
            {
                "ftp"     => new FluentFtpClientImpl(config),
                "ftps"    => new FluentFtpClientImpl(ApplyDefaultEncryption(config, "ExplicitTls")),
                "sftp"    => new SftpClientImpl(config),
                _         => throw new ArgumentException($"Unsupported FTP mode: {config.Mode}. Use Ftp, Ftps, or Sftp.")
            };
        }

        private static FtpConnectionConfig ApplyDefaultEncryption(FtpConnectionConfig config, string encryptionMode)
        {
            // Return a new config with the encryption mode set if not already specified.
            var copy = new FtpConnectionConfig
            {
                Host = config.Host,
                Port = config.Port,
                Mode = config.Mode,
                Username = config.Username,
                Password = config.Password,
                PasswordEnvVar = config.PasswordEnvVar,
                RemoteBasePath = config.RemoteBasePath,
                PassiveMode = config.PassiveMode,
                EncryptionMode = string.IsNullOrEmpty(config.EncryptionMode) ? encryptionMode : config.EncryptionMode,
                TimeoutSeconds = config.TimeoutSeconds
            };
            return copy;
        }
    }
}

