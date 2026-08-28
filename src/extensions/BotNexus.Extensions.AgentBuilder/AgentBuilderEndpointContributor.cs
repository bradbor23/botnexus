using System;
using System.IO;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.AgentBuilder;

/// <summary>
/// Serves the prebuilt Agent Builder SPA (static files bundled in <c>wwwroot/</c>) under
/// <c>/agent-builder</c>. Because the app is served by the gateway itself it is same-origin,
/// so its read panel calls <c>/api/agents</c> without any CORS configuration.
/// </summary>
public sealed class AgentBuilderEndpointContributor : IEndpointContributor
{
    private const string RoutePrefix = "/agent-builder";

    public void MapEndpoints(WebApplication app)
    {
        var extensionDir = Path.GetDirectoryName(typeof(AgentBuilderEndpointContributor).Assembly.Location)!;
        var webRoot = Path.Combine(extensionDir, "wwwroot");
        var indexPath = Path.Combine(webRoot, "index.html");

        if (!File.Exists(indexPath))
        {
            app.Services.GetService<ILogger<AgentBuilderEndpointContributor>>()?.LogWarning(
                "Agent Builder web root not found at {Path} — skipping {Prefix} registration.",
                indexPath, RoutePrefix);
            return;
        }

        var files = new PhysicalFileProvider(webRoot);

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (!path.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var relative = path[RoutePrefix.Length..];

            // "/agent-builder" (no trailing slash) → redirect so the SPA's relative asset
            // URLs resolve against "/agent-builder/" rather than "/".
            if (relative.Length == 0)
            {
                context.Response.Redirect(RoutePrefix + "/", permanent: false);
                return;
            }

            if (relative == "/")
                relative = "/index.html";

            var fileInfo = files.GetFileInfo(relative);

            // Client-side path with no file on disk and no extension → serve the SPA document.
            if ((!fileInfo.Exists || fileInfo.IsDirectory) && !relative.Contains('.'))
            {
                relative = "/index.html";
                fileInfo = files.GetFileInfo(relative);
            }

            if (!fileInfo.Exists || fileInfo.IsDirectory)
            {
                await next();
                return;
            }

            context.Response.ContentType = GetContentType(relative);

            // Vite fingerprints everything under /assets/, so those are immutable; the
            // index document must always revalidate so a redeploy is picked up immediately.
            context.Response.Headers.CacheControl =
                relative.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";

            await context.Response.SendFileAsync(fileInfo);
        });
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".wasm" => "application/wasm",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}
