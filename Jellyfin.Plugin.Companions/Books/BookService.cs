using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Companions.Configuration;
using Jellyfin.Plugin.Companions.Matching;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Companions.Books;

/// <summary>The books found for a title, and the servers that could not be asked.</summary>
public sealed record BookResult(DateTime FetchedAt, IReadOnlyList<BookHit> Books, IReadOnlyList<string> Errors, bool Adaptation, IReadOnlyList<string> SourceAuthors);

/// <summary>
/// Finds the book (or audiobook, or comic) a movie or series is based on, on the configured book servers.
/// <para>
/// Jellyfin keeps the source author from TMDB among the writers, with a role such as "Novel" or
/// "Short Story", and TMDB keywords such as "based on novel or book" as tags. With a source author a
/// book must be by that author and its title must fit the movie's (equal, or one contained in the
/// other: "The Fellowship of the Ring" in "The Lord of the Rings: The Fellowship of the Ring"). Without
/// one, only an exactly equal title counts.
/// </para>
/// </summary>
public sealed class BookService
{
    private static readonly string[] SourceRoleWords =
    {
        "novel", "book", "short story", "novella", "comic", "graphic novel", "manga", "light novel", "author", "memoir"
    };

    private static readonly string[] AdaptationTags =
    {
        "based on novel", "based on book", "based on short story", "based on novella", "based on comic", "based on manga",
        "based on light novel", "based on graphic novel", "based on memoir", "based on young adult novel", "based on children's book",
        "based on a novel", "based on a book", "literary adaptation", "romanverfilmung", "literaturverfilmung"
    };

    private readonly ILibraryManager _library;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<BookService> _logger;
    private readonly ConcurrentDictionary<Guid, BookResult> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, BookSource> _sources = new();

    public BookService(ILibraryManager library, IHttpClientFactory http, ILogger<BookService> logger)
    {
        _library = library;
        _http = http;
        _logger = logger;
        Plugin.ConfigurationSaved += (_, _) =>
        {
            _cache.Clear();
            _sources.Clear();
        };
    }

    /// <summary>Source authors from the credits ("Frank Herbert (Novel)").</summary>
    public List<string> SourceAuthors(BaseItem item)
    {
        return _library.GetPeople(item)
            .Where(p => p.Type == PersonKind.Writer && !string.IsNullOrWhiteSpace(p.Role)
                && SourceRoleWords.Any(w => p.Role!.Contains(w, StringComparison.OrdinalIgnoreCase))
                && !p.Role!.Contains("screenplay", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True if the item is an adaptation: a source author in the credits or a "based on …" tag.</summary>
    public static bool IsAdaptation(BaseItem item, IReadOnlyCollection<string> sourceAuthors)
    {
        if (sourceAuthors.Count > 0)
        {
            return true;
        }

        return item.Tags.Any(t => AdaptationTags.Any(a => t.Contains(a, StringComparison.OrdinalIgnoreCase)));
    }

    public BookServerConfig? Server(string id) => Plugin.Instance?.Configuration.BookServers.FirstOrDefault(s => s.Id == id);

    public BookSource SourceFor(BookServerConfig server)
    {
        var key = server.Id + "|" + server.Kind + "|" + server.Url + "|" + server.OpdsPath;
        return _sources.GetOrAdd(key, _ => BookSource.Create(server, NewClient()));
    }

    public HttpClient NewClient()
    {
        var client = _http.CreateClient("Companions");
        client.Timeout = TimeSpan.FromSeconds(8);
        return client;
    }

    /// <summary>The books for a movie or series: from the cache, or looked up now.</summary>
    public async Task<BookResult> GetAsync(BaseItem title, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var maxAge = TimeSpan.FromHours(Math.Max(1, cfg.CacheHours));
        if (_cache.TryGetValue(title.Id, out var cached) && DateTime.UtcNow - cached.FetchedAt < (cached.Errors.Count > 0 ? TimeSpan.FromMinutes(10) : maxAge))
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(title.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(title.Id, out cached) && DateTime.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(1))
            {
                return cached;
            }

            var result = await LookupAsync(title, cfg, ct).ConfigureAwait(false);
            _cache[title.Id] = result;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A cached book by server and id (for cover requests).</summary>
    public BookHit? FindCached(string serverId, string externalId)
    {
        foreach (var r in _cache.Values)
        {
            var hit = r.Books.FirstOrDefault(b => b.ServerId == serverId && b.ExternalId == externalId);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    public void ClearCache() => _cache.Clear();

    private async Task<BookResult> LookupAsync(BaseItem title, PluginConfiguration cfg, CancellationToken ct)
    {
        var authors = SourceAuthors(title);
        var adaptation = IsAdaptation(title, authors);
        var servers = cfg.BookServers.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (servers.Count == 0 || (cfg.OnlyAdaptations && !adaptation))
        {
            return new BookResult(DateTime.UtcNow, Array.Empty<BookHit>(), Array.Empty<string>(), adaptation, authors);
        }

        var variants = TitleKeys.TitleVariants(title.Name, title.OriginalTitle);
        var queries = new List<string>();
        foreach (var t in new[] { title.Name, title.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            queries.Add(t!);
            var main = TitleKeys.StripDecoration(t!.Split(new[] { ": ", " - ", " – " }, 2, StringSplitOptions.None)[0]);
            if (main.Length >= 3 && !queries.Contains(main, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(main);
            }
        }

        foreach (var surname in authors.Take(2).Select(TitleKeys.Surname).Where(s => s is not null).Distinct())
        {
            queries.Add(surname!);
        }

        var errors = new ConcurrentBag<string>();
        var found = new ConcurrentBag<BookHit>();
        await Parallel.ForEachAsync(servers, ct, async (server, token) =>
        {
            var source = SourceFor(server);
            foreach (var q in queries)
            {
                try
                {
                    foreach (var hit in await source.SearchAsync(q, token).ConfigureAwait(false))
                    {
                        if (Fits(hit, variants, authors, adaptation || !cfg.OnlyAdaptations))
                        {
                            found.Add(hit);
                        }
                    }
                }
                catch (BookSourceException ex)
                {
                    errors.Add(ex.Message);
                    _logger.LogWarning("Companions: {Message}", ex.Message);
                    if (ex.Code is "auth" or "network" or "timeout")
                    {
                        return; // the next query would fail the same way
                    }
                }
            }
        }).ConfigureAwait(false);

        var books = found
            .GroupBy(b => b.ServerId + "|" + b.ExternalId)
            .Select(g => g.First())
            .OrderBy(b => servers.FindIndex(s => s.Id == b.ServerId))
            .ThenBy(b => b.Kind == "audiobook" ? 1 : 0)
            .ThenBy(b => b.Title.Length)
            .Take(8)
            .ToList();
        return new BookResult(DateTime.UtcNow, books, errors.Distinct().ToList(), adaptation, authors);
    }

    internal static bool Fits(BookHit hit, IReadOnlyList<string> titleKeys, IReadOnlyList<string> sourceAuthors, bool titleOnlyAllowed)
    {
        var byAuthor = sourceAuthors.Count > 0 && hit.Authors.Any(a => sourceAuthors.Any(s => TitleKeys.SameAuthor(a, s)));
        if (byAuthor)
        {
            return TitleKeys.TitleFits(hit.Title, titleKeys, allowContainment: true);
        }

        // a known source author that differs from the book's author: another book with the same title
        if (sourceAuthors.Count > 0 && hit.Authors.Count > 0)
        {
            return false;
        }

        return titleOnlyAllowed && TitleKeys.TitleFits(hit.Title, titleKeys, allowContainment: false);
    }
}
