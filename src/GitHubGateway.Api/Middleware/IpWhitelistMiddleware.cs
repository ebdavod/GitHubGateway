using System;
using System.Net;
using System.Threading.Tasks;
using GitHubGateway.Application.Common;
using GitHubGateway.Infrastructure.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitHubGateway.Api.Middleware
{
    /// <summary>
    /// Rejects requests whose client IP is not on the configured whitelist, and optionally
    /// enforces an X-API-Key header for non-health endpoints. The middleware is bypassed
    /// when <see cref="IpWhitelistOptions.Enabled"/> is false.
    /// </summary>
    public sealed class IpWhitelistMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<IpWhitelistMiddleware> _logger;

        public IpWhitelistMiddleware(RequestDelegate next, ILogger<IpWhitelistMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IOptions<IpWhitelistOptions> optionsAccessor)
        {
            var opts = optionsAccessor.Value;
            if (!opts.Enabled)
            {
                await _next(context);
                return;
            }

            var path = context.Request.Path.Value ?? string.Empty;
            var isHealthEndpoint = path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWith("/api/info", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);

            // --- IP check ---
            var remoteIp = IpWhitelistMatcher.ResolveClientIp(
                context.Request.Headers["X-Forwarded-For"],
                context.Request.Headers["X-Real-IP"],
                context.Connection.RemoteIpAddress?.ToString());

            if (remoteIp == IPAddress.None)
            {
                // Likely invalid - treat as not whitelisted
                await WriteBlocked(context, "Could not determine client IP.", HttpStatusCode.Forbidden);
                return;
            }

            bool allowed = IpWhitelistMatcher.IsAllowed(remoteIp, opts.Networks, opts.AllowLocalhost);
            if (!allowed)
            {
                _logger.LogWarning("IP {Ip} rejected by whitelist while accessing {Path}", remoteIp, path);
                await WriteBlocked(context, "Your IP is not allowed.", HttpStatusCode.Forbidden);
                return;
            }

            // --- API key check (skipped for health endpoints if configured) ---
            if (opts.ApiKeys is { Length: > 0 })
            {
                if (isHealthEndpoint && opts.SkipApiKeyForHealthEndpoints)
                {
                    // Skip API key for health/info/swagger
                }
                else
                {
                    var provided = context.Request.Headers["X-API-Key"].ToString();
                    if (string.IsNullOrEmpty(provided) || !ArrayContains(opts.ApiKeys, provided))
                    {
                        _logger.LogWarning("Missing or invalid API key from IP {Ip} on {Path}", remoteIp, path);
                        await WriteBlocked(context, "Missing or invalid API key.", HttpStatusCode.Unauthorized);
                        return;
                    }
                }
            }

            await _next(context);
        }

        private static bool ArrayContains(string[] arr, string value)
        {
            foreach (var x in arr)
                if (string.Equals(x, value, StringComparison.Ordinal)) return true;
            return false;
        }

        private static Task WriteBlocked(HttpContext ctx, string message, HttpStatusCode status)
        {
            ctx.Response.StatusCode = (int)status;
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                status = (int)status,
                error = message,
                traceId = ctx.TraceIdentifier
            }));
        }
    }
}
