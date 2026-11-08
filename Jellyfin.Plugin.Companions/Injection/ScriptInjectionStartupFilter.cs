using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Companions.Injection;

/// <summary>
/// Adds the Companions stylesheet and script to jellyfin-web's index.html at request time.
/// <para>
/// Same technique Jellyfin Enhanced uses on Jellyfin 12: request-time middleware instead of
/// rewriting files in the web folder. That means no write access to jellyfin-web is required,
/// nothing survives an uninstall, and it stacks cleanly with other plugins that rewrite
/// index.html (Jellyfin Enhanced, File Transformation / Home Screen Sections, KefinTweaks
/// via JS Injector): every rewriter only appends its own tags and leaves the rest alone.
/// </para>
/// </summary>
public sealed class ScriptInjectionStartupFilter : IStartupFilter
{
    private const string Marker = "Companions/ClientScript";

    private readonly ILogger<ScriptInjectionStartupFilter> _logger;
    private int _loggedOnce;

    public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var plugin = Plugin.Instance;
        if (plugin is null
            || !plugin.Configuration.Enabled
            || !HttpMethods.IsGet(context.Request.Method)
            || !IsIndexRequest(context.Request.Path.Value))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Ask downstream for a complete, uncompressed 200 that we can rewrite.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
            && !context.Response.Headers.ContainsKey("Content-Encoding");

        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 4096, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            html = Inject(html, plugin.CacheKey);
            if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
            {
                _logger.LogInformation("Companions: client script injected into index.html");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Companions: injection failed, serving original index.html");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        context.Response.Headers.CacheControl = "no-cache";
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    internal static string Inject(string html, string cacheKey)
    {
        if (html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var v = Uri.EscapeDataString(cacheKey);
        var link = $"<link rel=\"stylesheet\" data-plugin=\"Companions\" href=\"../Companions/ClientStyle?v={v}\">";
        var script = $"<script plugin=\"Companions\" src=\"../{Marker}?v={v}\" defer></script>";

        var headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headClose >= 0)
        {
            html = html.Insert(headClose, link + "\n");
        }

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose >= 0)
        {
            html = html.Insert(bodyClose, script + "\n");
        }

        return html;
    }

    // "/web", "/web/" and "/web/index.html", also below a base URL such as "/jellyfin/web/".
    private static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase);
    }
}
