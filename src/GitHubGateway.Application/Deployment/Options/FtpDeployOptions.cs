using System;
using System.Collections.Generic;

namespace GitHubGateway.Application.Deployment.Options
{
    /// <summary>
    /// Top-level options for the FTP deployment subsystem.
    /// </summary>
    public sealed class FtpDeployOptions
    {
        public const string SectionName = "FtpDeploy";

        /// <summary>If false, all FTP endpoints return 503.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Default connection timeout (seconds).</summary>
        public int DefaultTimeoutSeconds { get; set; } = 60;

        /// <summary>Max parallel uploads per deployment job.</summary>
        public int MaxConcurrentUploads { get; set; } = 3;

        /// <summary>How many times to retry a failed upload.</summary>
        public int RetryCount { get; set; } = 3;

        /// <summary>Initial delay between retries (exponential backoff).</summary>
        public int RetryDelaySeconds { get; set; } = 5;

        /// <summary>Local directory used for ZIP extraction before upload.</summary>
        public string TempDir { get; set; } = "/tmp/ftp-deploy";

        /// <summary>How many past deployments to keep in history per target.</summary>
        public int HistoryRetentionCount { get; set; } = 10;

        /// <summary>List of allowed FTP hosts (whitelist). Empty = allow all.</summary>
        public string[] AllowedHosts { get; set; } = Array.Empty<string>();

        /// <summary>List of configured deployment targets.</summary>
        public List<DTOs.FtpDeployTarget> Targets { get; set; } = new();
    }
}
