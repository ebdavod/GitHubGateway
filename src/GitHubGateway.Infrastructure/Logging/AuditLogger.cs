using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHubGateway.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitHubGateway.Infrastructure.Logging
{
    /// <summary>
    /// Writes audit entries (one per line, JSON-lines format) to disk. Each entry contains
    /// the timestamp, client IP, method, path, status code, and the response payload size.
    ///
    /// Designed to be cheap and lock-free under typical load: a single async serializer
    /// with a SemaphoreSlim to serialize disk writes.
    /// </summary>
    public interface IAuditLogger
    {
        Task LogAsync(AuditEntry entry, CancellationToken ct = default);
    }

    public sealed class AuditEntry
    {
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
        public string ClientIp { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public int StatusCode { get; set; }
        public long ElapsedMs { get; set; }
        public string? Owner { get; set; }
        public string? Repo { get; set; }
        public string? CommitSha { get; set; }
        public string? Error { get; set; }
    }

    public sealed class FileAuditLogger : IAuditLogger
    {
        private readonly AuditOptions _options;
        private readonly ILogger<FileAuditLogger> _logger;
        private readonly SemaphoreSlim _sem = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

        public FileAuditLogger(IOptions<AuditOptions> options, ILogger<FileAuditLogger> logger)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task LogAsync(AuditEntry entry, CancellationToken ct = default)
        {
            if (!_options.Enabled) return;
            if (_options.WriteOnly && entry.Method is not ("POST" or "PUT" or "PATCH" or "DELETE")) return;

            try
            {
                var dir = Path.GetDirectoryName(_options.FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;
                await _sem.WaitAsync(ct);
                try
                {
                    await File.AppendAllTextAsync(_options.FilePath, json, ct);
                }
                finally
                {
                    _sem.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write audit entry.");
            }
        }
    }
}
