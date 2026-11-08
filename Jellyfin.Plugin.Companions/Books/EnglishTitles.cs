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
    private readonly ConcurrentDictionary<(Guid Id, string Language), (DateTime At, string? Title)> _cache = new();

    public EnglishTitles(IProviderManager providers, ILogger<EnglishTitles> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    /// <summary>The titles in the given languages ("en, de"), without duplicates and without the ones Jellyfin already has.</summary>
    public async Task<List<string>> GetManyAsync(BaseItem item, string languages, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var lang in (languages ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var title = await GetAsync(item, lang.Trim().ToLowerInvariant(), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(title)
                && !string.Equals(title, item.Name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(title, item.OriginalTitle, StringComparison.OrdinalIgnoreCase)
                && !result.Contains(title, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(title);
            }
        }

        return result;
    }

    public async Task<string?> GetAsync(BaseItem item, CancellationToken ct) => await GetAsync(item, "en", ct).ConfigureAwait(false);

    public async Task<string?> GetAsync(BaseItem item, string language, CancellationToken ct)
    {
        var country = language switch
        {
            "en" => "US", "de" => "DE", "fr" => "FR", "es" => "ES", "it" => "IT", "ja" => "JP", "pt" => "BR", "nl" => "NL",
            _ => language.ToUpperInvariant()
        };
        if (_cache.TryGetValue((item.Id, language), out var hit) && DateTime.UtcNow - hit.At < MaxAge)
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
                        SearchInfo = new SeriesInfo { Name = item.Name, Year = item.ProductionYear, ProviderIds = ids, MetadataLanguage = language, MetadataCountryCode = country },
                        SearchProviderName = "TheMovieDb",
                        IncludeDisabledProviders = true
                    },
                    ct).ConfigureAwait(false),
                Movie => await _providers.GetRemoteSearchResults<Movie, MovieInfo>(
                    new RemoteSearchQuery<MovieInfo>
                    {
                        SearchInfo = new MovieInfo { Name = item.Name, Year = item.ProductionYear, ProviderIds = ids, MetadataLanguage = language, MetadataCountryCode = country },
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
            _logger.LogDebug(ex, "Companions: no {Language} title for {Name}", language, item.Name);
        }

        var value = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        _cache[(item.Id, language)] = (DateTime.UtcNow, value);
        return value;
    }
}
