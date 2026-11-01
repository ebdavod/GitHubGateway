using System;

namespace GitHubGateway.Application.Common
{
    /// <summary>
    /// Strongly-typed options for the IP Whitelist middleware.
    /// </summary>
    public sealed class IpWhitelistOptions
    {
        public const string SectionName = "IpWhitelist";

        /// <summary>If false, the middleware is a no-op and all IPs are allowed.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Allow loopback (127.0.0.1, ::1) — typically true in development.</summary>
        public bool AllowLocalhost { get; set; } = true;

        /// <summary>List of allowed networks in CIDR form (e.g. 192.168.1.0/24) or single IPs.</summary>
        public string[] Networks { get; set; } = Array.Empty<string>();

        /// <summary>If non-empty, the request must carry one of these API keys in the X-API-Key header.</summary>
        public string[] ApiKeys { get; set; } = Array.Empty<string>();

        /// <summary>When true, the API key check is bypassed for health/info endpoints.</summary>
        public bool SkipApiKeyForHealthEndpoints { get; set; } = true;
    }

    /// <summary>
    /// Options for the GitHub client (PAT, base URL, caching, etc.).
    /// </summary>
    public sealed class GitHubOptions
    {
        public const string SectionName = "GitHub";

        /// <summary>Personal Access Token. Reads from env var GITHUB_TOKEN as a fallback.</summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>GitHub API base URL. Use https://github.example.com/api/v3 for Enterprise.</summary>
        public string ApiUrl { get; set; } = "https://api.github.com";

        /// <summary>User-Agent header sent with every GitHub request.</summary>
        public string UserAgent { get; set; } = "GitHubGateway/1.0";

        /// <summary>Cache TTL (seconds) for read operations.</summary>
        public int CacheTtlSeconds { get; set; } = 60;

        /// <summary>HTTP timeout in seconds when calling GitHub.</summary>
        public int HttpTimeoutSeconds { get; set; } = 30;
    }

    /// <summary>
    /// Global rate-limiting options for the gateway itself (independent of GitHub's rate limit).
    /// </summary>
    public sealed class RateLimitOptions
    {
        public const string SectionName = "RateLimiting";

        public bool Enabled { get; set; } = true;

        /// <summary>Maximum requests per window per client IP.</summary>
        public int PerIpLimit { get; set; } = 100;

        /// <summary>Window in seconds.</summary>
        public int WindowSeconds { get; set; } = 60;
    }

    /// <summary>
    /// Audit-log options.
    /// </summary>
    public sealed class AuditOptions
    {
        public const string SectionName = "Audit";

        public bool Enabled { get; set; } = true;

        /// <summary>Path to the JSON-lines audit log file.</summary>
        public string FilePath { get; set; } = "logs/audit.jsonl";

        /// <summary>If true, only write operations (POST/PUT/DELETE) are logged.</summary>
        public bool WriteOnly { get; set; } = true;
    }
}
