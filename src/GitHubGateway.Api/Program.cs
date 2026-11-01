using System;
using System.IO;
using FluentValidation;
using GitHubGateway.Api.Middleware;
using GitHubGateway.Application.Validators;
using GitHubGateway.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Serilog;

// --- Bootstrap Serilog (reads Logging:Serilog section) ---
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .Build())
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    Log.Information("Starting GitHubGateway.Api");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog as the logging provider
    builder.Host.UseSerilog();

    // --- Configuration binding ---
    builder.Services.AddOptions();
    builder.Services.AddInfrastructure(builder.Configuration);

    // --- FluentValidation ---
    builder.Services.AddScoped<UpdateFileRequestValidator>();
    builder.Services.AddScoped<DeleteFileRequestValidator>();
    builder.Services.AddScoped<CreateBranchRequestValidator>();
    builder.Services.AddScoped<CreatePullRequestRequestValidator>();
    builder.Services.AddScoped<ApplyPatchRequestValidator>();

    // --- Controllers + Swagger ---
    builder.Services.AddControllers(opts =>
    {
        // Place the IP whitelist and rate-limit middleware ordering is handled in the pipeline below.
    });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "GitHub Gateway API",
            Version = "v1",
            Description = "A secure, IP-whitelisted gateway in front of the GitHub REST API. " +
                          "Stores the Personal Access Token on the server; clients never see it.",
            Contact = new OpenApiContact { Name = "Operations", Email = "ops@example.com" }
        });

        // X-API-Key header (optional)
        c.OperationFilter<ApiKeyHeaderOperationFilter>();

        // Support{* path} routes correctly
        c.OperationFilter<CustomOperationIdFilter>();
    });

    // --- CORS (allow all for demo; lock down in production) ---
    builder.Services.AddCors(o => o.AddPolicy("Default", p =>
        p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

    // --- Health checks (liveness + readiness for GitHub) ---
    builder.Services.AddHealthChecks()
        .AddCheck<GitHubHealthCheck>("github", tags: new[] { "ready" });

    var app = builder.Build();

    // --- Pipeline ---
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "GitHub Gateway API v1"));
    }
    else
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "GitHub Gateway API v1"));
    }

    app.UseSerilogRequestLogging();
    app.UseCors("Default");

    app.UseMiddleware<ExceptionMiddleware>();
    app.UseMiddleware<IpWhitelistMiddleware>();
    app.UseMiddleware<RateLimitMiddleware>();

    app.UseRouting();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHealthChecks("/api/health");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// --- Helper classes used by the Swagger configuration above ---

internal sealed class ApiKeyHeaderOperationFilter : Swashbuckle.AspNetCore.SwaggerGen.IOperationFilter
{
    public void Apply(Microsoft.OpenApi.OpenApiOperation operation,
        Swashbuckle.AspNetCore.SwaggerGen.OperationFilterContext context)
    {
        operation.Parameters ??= new System.Collections.Generic.List<Microsoft.OpenApi.IOpenApiParameter>();
        operation.Parameters.Add(new Microsoft.OpenApi.OpenApiParameter
        {
            Name = "X-API-Key",
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Required = false,
            Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String },
            Description = "Optional API key (only enforced if ApiKeys is non-empty in config)."
        });
    }
}

internal sealed class CustomOperationIdFilter : Swashbuckle.AspNetCore.SwaggerGen.IOperationFilter
{
    public void Apply(Microsoft.OpenApi.OpenApiOperation operation,
        Swashbuckle.AspNetCore.SwaggerGen.OperationFilterContext context)
    {
        if (string.IsNullOrEmpty(operation.OperationId) && context.MethodInfo != null)
        {
            operation.OperationId = $"{context.MethodInfo.DeclaringType?.Name}_{context.MethodInfo.Name}";
        }
    }
}

internal sealed class GitHubHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly GitHubGateway.Application.Interfaces.IGitHubRepositoryService _service;
    public GitHubHealthCheck(GitHubGateway.Application.Interfaces.IGitHubRepositoryService service) => _service = service;
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var info = await _service.GetConnectionInfoAsync(cancellationToken);
        var data = new System.Collections.Generic.Dictionary<string, object>
        {
            ["authenticated"] = info.Authenticated,
            ["userName"] = info.UserName ?? string.Empty,
            ["rateLimitRemaining"] = info.RateLimitRemaining
        };
        return info.Authenticated
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("GitHub reachable.", data)
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("GitHub not reachable.", data: data);
    }
}
