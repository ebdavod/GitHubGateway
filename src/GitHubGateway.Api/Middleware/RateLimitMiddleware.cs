using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GitHubGateway.Api.Middleware
{
    /// <summary>
    /// Simple in-memory per-IP rate limiter. Uses a sliding window based on the configured
    /// window length and limit. This is intentionally lightweight — for distributed
    /// deployments you should swap this out with AspNetCoreRateLimit + Redis.
    /// </summary>
    public sealed class RateLimitMiddleware
    {
        private readonly RequestDelegate _next;

        public RateLimitMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, IOptions<Application.Common.RateLimitOptions> optionsAccessor)
        {
            var opts = optionsAccessor.Value;
            if (!opts.Enabled)
            {
                await _next(context);
                return;
            }

            var ip = (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            var key = (ip, window: (int)(System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() / opts.WindowSeconds));

            var bucket = RateLimitStore.GetOrCreate(key, opts.PerIpLimit);
            if (!bucket.TryAcquire())
            {
                context.Response.StatusCode = 429;
                context.Response.ContentType = "application/json";
                context.Response.Headers["Retry-After"] = opts.WindowSeconds.ToString();
                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = 429,
                    error = "Rate limit exceeded. Try again later.",
                    retryAfterSeconds = opts.WindowSeconds
                }));
                return;
            }

            await _next(context);
        }
    }

    internal static class RateLimitStore
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), TokenBucket> _buckets = new();
        private static long _lastCleanup = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static TokenBucket GetOrCreate((string ip, int window) key, int capacity)
        {
            // Periodic cleanup
            var now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var lastCleanup = System.Threading.Interlocked.Read(ref _lastCleanup);
            if (now - lastCleanup > 300)   // 5 minutes
            {
                System.Threading.Interlocked.Exchange(ref _lastCleanup, now);
                CleanupOld(now - 600);
            }
            return _buckets.GetOrAdd(key, _ => new TokenBucket(capacity));
        }

        private static void CleanupOld(long cutoffWindowIndex)
        {
            // Best-effort cleanup - tuple element name access requires positional Item2.
            foreach (var k in _buckets.Keys)
            {
                if (k.Item2 < (int)(cutoffWindowIndex / 1))   // Approximation - any older than 10 minutes ago
                {
                    _buckets.TryRemove(k, out _);
                }
            }
        }
    }

    internal sealed class TokenBucket
    {
        private int _available;
        public TokenBucket(int capacity) => _available = capacity;
        public bool TryAcquire()
        {
            // Simple atomic decrement
            int current;
            do
            {
                current = _available;
                if (current <= 0) return false;
            } while (System.Threading.Interlocked.CompareExchange(ref _available, current - 1, current) != current);
            return true;
        }
    }
}
