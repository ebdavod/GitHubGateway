using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using GitHubGateway.Application.Deployment.DTOs;
using GitHubGateway.Application.Deployment.Interfaces;

namespace GitHubGateway.Infrastructure.Deployment
{
    /// <summary>
    /// In-memory store for live deployment status. Tracks both per-target and per-job status.
    /// </summary>
    public sealed class DeploymentStatusStore : IDeploymentStatusStore
    {
        private readonly ConcurrentDictionary<string, DeployStatusResponse> _byTarget = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DeployStatusResponse> _byJob = new(StringComparer.OrdinalIgnoreCase);

        public DeployStatusResponse? Get(string targetName, string? jobId)
        {
            if (!string.IsNullOrEmpty(jobId) && _byJob.TryGetValue(jobId, out var s1)) return s1;
            if (_byTarget.TryGetValue(targetName, out var s2)) return s2;
            return null;
        }

        public void Update(string targetName, DeployStatusResponse status)
        {
            _byTarget[targetName] = status;
            if (!string.IsNullOrEmpty(status.JobId))
                _byJob[status.JobId] = status;
        }

        public void Clear(string targetName)
        {
            _byTarget.TryRemove(targetName, out _);
        }
    }
}
