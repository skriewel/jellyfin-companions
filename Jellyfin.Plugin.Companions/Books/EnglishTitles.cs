using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Companions.Books;

/// <summary>
/// The English title of a movie or series, looked up through Jellyfin's own TMDB provider (no key of our
/// own). Jellyfin stores only the title in the library's language and the original title: an anime shows
/// up as "Atelier of Witch Hat" / "とんがり帽子のアトリエ", while the manga on the book server is "Witch Hat Atelier".
/// </summary>
public sealed class EnglishTitles
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly IProviderManager _providers;
    private readonly ILogger<EnglishTitles> _logger;
    private readonly ConcurrentDictionary<Guid, (DateTime At, string? Title)> _cache = new();

    public EnglishTitles(IProviderManager providers, ILogger<EnglishTitles> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task<string?> GetAsync(BaseItem item, CancellationToken ct)
    {
        if (_cache.TryGetValue(item.Id, out var hit) && DateTime.UtcNow - hit.At < MaxAge)
        {
            return hit.Title;
        }

        if (!item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdb) || string.IsNullOrEmpty(tmdb))
        {
            return null;
        }

        string? title = null;
        try
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [MetadataProvider.Tmdb.ToString()] = tmdb };
            IEnumerable<RemoteSearchResult> results = item switch
            {
                Series => await _providers.GetRemoteSearchResults<Series, SeriesInfo>(
                    new RemoteSearchQuery<SeriesInfo>
                    {
                        SearchInfo = new SeriesInfo { Name = item.Name, Year = item.ProductionYear, ProviderIds = ids, MetadataLanguage = "en", MetadataCountryCode = "US" },
                        SearchProviderName = "TheMovieDb",
                        IncludeDisabledProviders = true
                    },
                    ct).ConfigureAwait(false),
                Movie => await _providers.GetRemoteSearchResults<Movie, MovieInfo>(
                    new RemoteSearchQuery<MovieInfo>
                    {
                        SearchInfo = new MovieInfo { Name = item.Name, Year = item.ProductionYear, ProviderIds = ids, MetadataLanguage = "en", MetadataCountryCode = "US" },
                        SearchProviderName = "TheMovieDb",
                        IncludeDisabledProviders = true
                    },
                    ct).ConfigureAwait(false),
                _ => Enumerable.Empty<RemoteSearchResult>()
            };

            var list = results.ToList();
            title = (list.FirstOrDefault(r => r.ProviderIds is not null && r.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var id) && id == tmdb)
                ?? list.FirstOrDefault())?.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Companions: no English title for {Name}", item.Name);
        }

        _cache[item.Id] = (DateTime.UtcNow, string.IsNullOrWhiteSpace(title) ? null : title.Trim());
        return _cache[item.Id].Title;
    }
}
