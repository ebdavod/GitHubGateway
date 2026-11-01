using System;
using GitHubGateway.Application.Common;
using GitHubGateway.Application.Interfaces;
using GitHubGateway.Infrastructure.GitHub;
using GitHubGateway.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Octokit;

namespace GitHubGateway.Infrastructure
{
    /// <summary>
    /// Extension methods for registering Infrastructure-layer services in the DI container.
    /// </summary>
    public static class DependencyInjection
    {
        /// <summary>
        /// Adds GitHub client, caching, audit logging and configuration bindings to the container.
        /// </summary>
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
        {
            // Options bindings
            services.Configure<GitHubOptions>(config.GetSection(GitHubOptions.SectionName));
            services.Configure<IpWhitelistOptions>(config.GetSection(IpWhitelistOptions.SectionName));
            services.Configure<RateLimitOptions>(config.GetSection(RateLimitOptions.SectionName));
            services.Configure<AuditOptions>(config.GetSection(AuditOptions.SectionName));

            // Memory cache for read-through caching on the GitHub client
            services.AddMemoryCache();

            // Octokit GitHub client
            services.AddSingleton<GitHubClient>(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
                var token = !string.IsNullOrWhiteSpace(opts.Token)
                    ? opts.Token
                    : Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException(
                        "GitHub PAT is not configured. Set 'GitHub:Token' in appsettings.json or the GITHUB_TOKEN environment variable.");

                // Parse "ProductName/x.y[.z]" into ProductHeaderValue.
                // Octokit's ProductHeaderValue does not accept pre-release suffixes like "1.0-dev".
                var (prodName, prodVer) = ParseUserAgent(opts.UserAgent);

                var productHeader = !string.IsNullOrEmpty(prodVer)
                    ? new ProductHeaderValue(prodName, prodVer)
                    : new ProductHeaderValue(prodName);

                // For GitHub Enterprise, use the constructor that accepts a base address;
                // for the public github.com, use the simple constructor.
                GitHubClient client;
                if (!string.IsNullOrWhiteSpace(opts.ApiUrl) && opts.ApiUrl != "https://api.github.com")
                {
                    client = new GitHubClient(productHeader, new Uri(opts.ApiUrl.TrimEnd('/') + "/"));
                }
                else
                {
                    client = new GitHubClient(productHeader);
                }
                client.Credentials = new Credentials(token);
                return client;
            });

            // Application-level GitHub service
            services.AddSingleton<IGitHubGatewayClient, OctokitGitHubClient>();
            services.AddScoped<IGitHubRepositoryService, Application.Services.GitHubRepositoryService>();

            // Audit logger
            services.AddSingleton<IAuditLogger, FileAuditLogger>();

            // --- FTP Deployment services ---
            services.AddFtpDeployment(config);

            return services;
        }

        /// <summary>
        /// Registers all FTP-deployment services (in this assembly) into the DI container.
        /// </summary>
        public static IServiceCollection AddFtpDeployment(this IServiceCollection services, IConfiguration config)
        {
            // Options binding for FtpDeploy
            services.Configure<GitHubGateway.Application.Deployment.Options.FtpDeployOptions>(
                config.GetSection(GitHubGateway.Application.Deployment.Options.FtpDeployOptions.SectionName));

            // FTP client factory (singleton — creates clients per request)
            services.AddSingleton<GitHubGateway.Application.Deployment.Interfaces.IFtpClientFactory, Ftp.FtpClientFactory>();

            // Artifact fetcher (depends on IGitHubGatewayClient)
            services.AddSingleton<GitHubGateway.Application.Deployment.Interfaces.IArtifactFetcher, Deployment.ArtifactFetcher>();

            // ZIP extractor
            services.AddSingleton<GitHubGateway.Application.Deployment.Interfaces.IZipExtractor, Deployment.ZipExtractor>();

            // Status & history stores
            services.AddSingleton<GitHubGateway.Application.Deployment.Interfaces.IDeploymentStatusStore, Deployment.DeploymentStatusStore>();
            services.AddSingleton<GitHubGateway.Application.Deployment.Interfaces.IDeploymentHistoryStore, Deployment.JsonFileDeploymentHistoryStore>();

            // Orchestrator (singleton — holds running-job state)
            services.AddSingleton<GitHubGateway.Application.Deployment.Interfaces.IDeploymentOrchestrator, Deployment.DeploymentOrchestrator>();

            return services;
        }

        /// <summary>
        /// Parses a User-Agent string like "GitHubGateway/1.0" into (name, version).
        /// The version portion must consist only of digits and dots (HTTP token rules).
        /// Any non-conforming suffix (e.g. "-dev", "+build") is stripped.
        /// </summary>
        private static (string name, string version) ParseUserAgent(string userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return ("GitHubGateway", "1.0");
            var slash = userAgent.IndexOf('/');
            string name = slash > 0 ? userAgent.Substring(0, slash) : userAgent;
            string version = slash > 0 && slash < userAgent.Length - 1
                ? userAgent.Substring(slash + 1)
                : string.Empty;

            // Strip any pre-release / build metadata that violates HTTP token rules.
            if (!string.IsNullOrEmpty(version))
            {
                int cut = version.IndexOfAny(new[] { '-', '+', '_', '~' });
                if (cut >= 0) version = version.Substring(0, cut);
            }

            name = name.Trim();
            version = version.Trim();
            return (string.IsNullOrEmpty(name) ? "GitHubGateway" : name,
                    string.IsNullOrEmpty(version) ? "1.0" : version);
        }
    }
}
