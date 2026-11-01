# GitHubGateway

A thin .NET 8 ASP.NET Core API gateway in front of the GitHub REST API.

## Purpose

The gateway stores the GitHub Personal Access Token (PAT) on the server and exposes
a curated subset of the GitHub API to authenticated clients. Clients never see the
PAT itself.

## Architecture

- `GitHubGateway.Api` - Controllers (Repositories, FileContent, FtpDeploy, Artifacts), Middleware (rate limit, IP whitelist, exception)
- `GitHubGateway.Application` - DTOs, Interfaces, Services (GitHubRepositoryService, DeploymentOrchestrator)
- `GitHubGateway.Infrastructure` - Octokit-based IGitHubGatewayClient, FTP/SFTP clients, Deployment orchestrator impl

## Build

```bash
dotnet build -c Release
dotnet publish src/GitHubGateway.Api/GitHubGateway.Api.csproj -c Release -r win-x64 --self-contained false -o ./publish
```

## Configuration

`appsettings.json`:

```json
{
  "GitHub": {
    "Token": "REPLACE_WITH_YOUR_GITHUB_PAT",
    "ApiUrl": "https://api.github.com",
    "UserAgent": "GitHubGateway/1.0",
    "CacheTtlSeconds": 60,
    "HttpTimeoutSeconds": 30
  },
  "IpWhitelist": {
    "Enabled": true,
    "ApiKeys": ["ggk_live_..."]
  }
}
```

## Endpoints

- `GET /api/info` - Gateway connection info
- `GET /api/v1/repos/{owner}/{repo}/branches` - List branches
- `GET /api/v1/repos/{owner}/{repo}/contents/{*path}` - List/get contents
- `PUT /api/v1/repos/{owner}/{repo}/contents/{*path}` - Create/update file (IIS-blocked for paths with dots)
- `DELETE /api/v1/repos/{owner}/{repo}/contents/{*path}` - Delete file
- `POST /api/v1/repos/{owner}/{repo}/file-content/get` - Get file by path in body (IIS workaround)
- `POST /api/v1/repos/{owner}/{repo}/file-content/list` - List directory by path in body
- `POST /api/v1/repos/{owner}/{repo}/file-content/upload` - Create/update file by path in body (text content)
- `POST /api/v1/repos/{owner}/{repo}/file-content/delete` - Delete file by path in body
- `POST /api/v1/repos/{owner}/{repo}/patch` - Apply a git unified diff
- `POST /api/v1/repos/{owner}/{repo}/branches` - Create branch
- `POST /api/v1/repos/{owner}/{repo}/pulls` - Open PR
- `POST /api/v1/repos/{owner}/{repo}/artifacts/upload` - **IIS-safe binary upload** (e.g. release ZIPs)
- `GET /api/v1/deploy/ftp/targets` - List FTP deploy targets
- `POST /api/v1/deploy/ftp/targets/{name}/deploy` - Trigger FTP deployment

## License

Proprietary - AvvalOnline.ir
