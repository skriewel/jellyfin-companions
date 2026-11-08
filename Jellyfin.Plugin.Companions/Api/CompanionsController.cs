using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Companions.Books;
using Jellyfin.Plugin.Companions.Configuration;
using Jellyfin.Plugin.Companions.Soundtracks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Companions.Api;

[ApiController]
[Route("Companions")]
public class CompanionsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SoundtrackIndex _soundtracks;
    private readonly BookService _books;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    public CompanionsController(SoundtrackIndex soundtracks, BookService books, ILibraryManager library, IUserManager users)
    {
        _soundtracks = soundtracks;
        _books = books;
        _library = library;
        _users = users;
    }

    // ------------------------------------------------------------------ client assets (anonymous: loaded before login)

    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var cfg = plugin.Configuration;
        var clientConfig = new { cfg.Enabled, Version = plugin.CacheKey };
        var script = "window.CompanionsConfig = " + JsonSerializer.Serialize(clientConfig, JsonOptions) + ";\n"
            + ReadResource("Web.companions.js");
        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(script, "application/javascript; charset=utf-8");
    }

    [HttpGet("ClientStyle")]
    [AllowAnonymous]
    [Produces("text/css")]
    public ActionResult GetClientStyle()
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(ReadResource("Web.companions.css"), "text/css; charset=utf-8");
    }

    // ------------------------------------------------------------------ data for a details page

    /// <summary>
    /// For a movie or series (seasons and episodes: their series): soundtrack albums and books.
    /// For a music album: the movies and series it is the soundtrack of.
    /// </summary>
    [HttpGet("Item/{itemId}")]
    [Authorize]
    public async Task<ActionResult> GetItem([FromRoute] Guid itemId, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var user = CurrentUser();
        var item = _library.GetItemById(itemId);
        if (item is null || (user is not null && !item.IsVisible(user)))
        {
            return NotFound();
        }

        if (item is MusicAlbum album)
        {
            var titles = cfg.ShowSoundtracks ? Visible(_soundtracks.TitlesFor(album), user) : new List<Guid>();
            return Json(new { kind = "album", titles });
        }

        BaseItem? target = item switch
        {
            Episode e => e.Series,
            Season s => s.Series,
            Movie or Series => item,
            _ => null
        };
        if (target is null)
        {
            return Json(new { kind = "unsupported" });
        }

        var soundtracks = cfg.ShowSoundtracks ? Visible(_soundtracks.AlbumsFor(target), user) : new List<Guid>();

        object[] books = Array.Empty<object>();
        string[] errors = Array.Empty<string>();
        if (cfg.ShowBooks && cfg.BookServers.Any(s => s.Enabled))
        {
            var result = await _books.GetAsync(target, ct).ConfigureAwait(false);
            books = result.Books.Select(b =>
            {
                var server = _books.Server(b.ServerId);
                return (object)new
                {
                    server = string.IsNullOrWhiteSpace(server?.Name) ? server?.Kind : server!.Name,
                    serverKind = server?.Kind,
                    kind = b.Kind,
                    title = b.Title,
                    authors = b.Authors,
                    volumes = b.Volumes,
                    url = b.Url,
                    cover = b.CoverUrl is null ? null : $"Companions/Cover/{b.ServerId}/{Uri.EscapeDataString(b.ExternalId)}"
                };
            }).ToArray();

            // only administrators learn why a server failed
            if (User.IsInRole("Administrator"))
            {
                errors = result.Errors.ToArray();
            }
        }

        return Json(new
        {
            kind = target is Series ? "series" : "movie",
            scope = target == item ? "self" : "parentSeries",
            soundtracks,
            books,
            errors
        });
    }

    /// <summary>
    /// The cover of a book found earlier, fetched from the book server with its credentials.
    /// Anonymous because &lt;img&gt; cannot send Jellyfin's token; only books already in the cache are served.
    /// </summary>
    [HttpGet("Cover/{serverId}/{bookId}")]
    [AllowAnonymous]
    public async Task<ActionResult> GetCover([FromRoute] string serverId, [FromRoute] string bookId, CancellationToken ct)
    {
        var hit = _books.FindCached(serverId, bookId);
        var server = _books.Server(serverId);
        if (hit?.CoverUrl is null || server is null)
        {
            return NotFound();
        }

        var cover = await _books.FetchCoverAsync(hit, server, ct).ConfigureAwait(false);
        if (cover.Bytes is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=86400";
        return File(cover.Bytes, cover.ContentType!);
    }

    // ------------------------------------------------------------------ admin

    /// <summary>Checks one book server with the settings from the form (not yet saved).</summary>
    [HttpPost("TestServer")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> TestServer([FromBody] BookServerConfig server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
        {
            return Json(new { ok = false, message = "No address." });
        }

        // a stored password is not sent back to the form; use the saved one when the field was left empty
        var saved = _books.Server(server.Id);
        if (saved is not null)
        {
            if (string.IsNullOrEmpty(server.Password))
            {
                server.Password = saved.Password;
            }

            if (string.IsNullOrEmpty(server.ApiKey))
            {
                server.ApiKey = saved.ApiKey;
            }
        }

        try
        {
            var source = BookSource.Create(server, _books.NewClient());
            var message = await source.TestAsync(ct).ConfigureAwait(false);
            return Json(new { ok = true, message });
        }
        catch (BookSourceException ex)
        {
            return Json(new { ok = false, message = ex.Message });
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            return Json(new { ok = false, message = "Invalid address: " + ex.Message });
        }
    }

    /// <summary>What the plugin finds for one title, with the reasons (for checking the matching).</summary>
    [HttpGet("Explain/{itemId}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Explain([FromRoute] Guid itemId, CancellationToken ct)
    {
        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var authors = _books.SourceAuthors(item);
        var result = item is Movie or Series ? await _books.GetAsync(item, ct).ConfigureAwait(false) : null;
        var covers = new List<object>();
        foreach (var b in result?.Books ?? Array.Empty<BookHit>())
        {
            var server = _books.Server(b.ServerId);
            if (server is null || b.CoverUrl is null)
            {
                covers.Add(new { b.Title, status = "no cover url" });
                continue;
            }

            var c = await _books.FetchCoverAsync(b, server, ct).ConfigureAwait(false);
            covers.Add(new { b.Title, status = c.Detail, bytes = c.Bytes?.Length });
        }
        return Json(new
        {
            name = item.Name,
            originalTitle = item.OriginalTitle,
            sourceAuthors = authors,
            englishTitle = result?.EnglishTitle,
            otherTitles = result?.ExtraTitles,
            queries = result?.Queries,
            adaptation = BookService.IsAdaptation(item, authors),
            tags = item.Tags.Where(t => t.StartsWith("based on", StringComparison.OrdinalIgnoreCase)),
            soundtracks = item is MusicAlbum a ? _soundtracks.TitlesFor(a) : _soundtracks.AlbumsFor(item),
            books = result?.Books,
            covers,
            notLinked = result?.Rejected,
            errors = result?.Errors
        });
    }

    [HttpPost("ClearCache")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ClearCache()
    {
        _books.ClearCache();
        _soundtracks.Invalidate();
        return NoContent();
    }

    private User? CurrentUser()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var userId) ? _users.GetUserById(userId) : null;
    }

    private List<Guid> Visible(IEnumerable<Guid> ids, User? user)
    {
        return ids
            .Select(id => _library.GetItemById(id))
            .Where(i => i is not null && (user is null || i.IsVisible(user)))
            .Select(i => i!.Id)
            .ToList();
    }

    // Jellyfin's own formatter writes PascalCase; the client script expects camelCase.
    private ContentResult Json(object value)
    {
        Response.Headers.CacheControl = "no-store";
        return Content(JsonSerializer.Serialize(value, JsonOptions), "application/json; charset=utf-8");
    }

    private static string ReadResource(string relativeName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"{typeof(Plugin).Namespace}.{relativeName}";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
