using System;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using GitHubGateway.Infrastructure.GitHub;
using GitHubGateway.Infrastructure.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GitHubGateway.Api.Middleware
{
    /// <summary>
    /// Central exception handler. Catches <see cref="GitHubGatewayException"/> and turns them
    /// into appropriate HTTP responses, and logs a unified error envelope for any other exception.
    /// Also writes an audit record on completion for write operations.
    /// </summary>
    public sealed class ExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionMiddleware> _logger;

        public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var sw = Stopwatch.StartNew();
            string? owner = null, repo = null, sha = null, error = null;
            try
            {
                // Stash owner/repo from route values for audit
                if (context.Request.RouteValues.TryGetValue("owner", out var o) && o is string os) owner = os;
                if (context.Request.RouteValues.TryGetValue("repo",  out var r) && r is string rs) repo = rs;

                await _next(context);
                sw.Stop();
                await WriteAuditAsync(context, sw.ElapsedMilliseconds, owner, repo, sha, error);
            }
            catch (GitHubGatewayException ex)
            {
                sw.Stop();
                error = ex.Message;
                _logger.LogWarning(ex, "GitHub gateway error on {Path}", context.Request.Path);
                await WriteJsonAsync(context, ex.StatusCode, ex.Message);
                await WriteAuditAsync(context, sw.ElapsedMilliseconds, owner, repo, sha, error);
            }
            catch (Exception ex)
            {
                sw.Stop();
                error = ex.Message;
                _logger.LogError(ex, "Unhandled exception on {Path}", context.Request.Path);
                // Always dump the exception to a fallback file in case Serilog is misconfigured.
                try
                {
                    System.IO.File.AppendAllText("logs/crash.log",
                        $"{System.DateTimeOffset.UtcNow:O} [{context.Request.Method} {context.Request.Path}] {ex}{System.Environment.NewLine}");
                }
                catch { /* best effort */ }
                // Expose the actual exception message in production too (temporary, for debugging).
                var message = ex.ToString();
                await WriteJsonAsync(context, HttpStatusCode.InternalServerError, message);
                await WriteAuditAsync(context, sw.ElapsedMilliseconds, owner, repo, sha, error);
            }
        }

        private static Task WriteJsonAsync(HttpContext ctx, HttpStatusCode status, string message)
        {
            ctx.Response.StatusCode = (int)status;
            ctx.Response.ContentType = "application/json";
            var body = JsonSerializer.Serialize(new
            {
                status = (int)status,
                error = message,
                traceId = ctx.TraceIdentifier
            });
            return ctx.Response.WriteAsync(body);
        }

        private static async Task WriteAuditAsync(HttpContext ctx, long elapsedMs,
            string? owner, string? repo, string? sha, string? error)
        {
            try
            {
                // Best-effort audit. We need a fresh scope because the request scope may be disposed.
                using var scope = ctx.RequestServices.CreateScope();
                var audit = scope.ServiceProvider.GetService<IAuditLogger>();
                if (audit is null) return;

                var remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
                await audit.LogAsync(new AuditEntry
                {
                    ClientIp = remoteIp,
                    Method = ctx.Request.Method,
                    Path = ctx.Request.Path,
                    StatusCode = ctx.Response.StatusCode,
                    ElapsedMs = elapsedMs,
                    Owner = owner,
                    Repo = repo,
                    CommitSha = sha,
                    Error = error
                });
            }
            catch
            {
                // Audit is best-effort; swallow failures.
            }
        }
    }
}
