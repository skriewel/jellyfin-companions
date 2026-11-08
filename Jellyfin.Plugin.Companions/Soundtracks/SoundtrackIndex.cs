using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Companions.Matching;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Companions.Soundtracks;

/// <summary>
/// Matches music albums to movies and series, in both directions. An album counts as the soundtrack of
/// a title when its name, without the soundtrack decoration, is that title (name or original title),
/// it is marked as a soundtrack (by its name or its genre), and the years fit.
/// </summary>
public sealed partial class SoundtrackIndex
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> SoundtrackGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "Soundtrack", "Soundtracks", "Score", "Film Score", "Filmmusik", "Original Score", "Film Soundtrack",
        "Movie Soundtrack", "TV Soundtrack", "Television Soundtrack", "OST"
    };

    private readonly ILibraryManager _library;
    private readonly object _lock = new();
    private Snapshot? _snapshot;

    public SoundtrackIndex(ILibraryManager library)
    {
        _library = library;
    }

    /// <summary>Soundtrack albums for a movie or series, best match first.</summary>
    public IReadOnlyList<Guid> AlbumsFor(BaseItem title)
    {
        var snap = Get();
        return snap.TitleToAlbums.TryGetValue(title.Id, out var list) ? list : Array.Empty<Guid>();
    }

    /// <summary>Movies and series an album is the soundtrack of.</summary>
    public IReadOnlyList<Guid> TitlesFor(MusicAlbum album)
    {
        var snap = Get();
        return snap.AlbumToTitles.TryGetValue(album.Id, out var list) ? list : Array.Empty<Guid>();
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _snapshot = null;
        }
    }

    private Snapshot Get()
    {
        lock (_lock)
        {
            if (_snapshot is null || DateTime.UtcNow - _snapshot.Built > MaxAge)
            {
                _snapshot = Build();
            }

            return _snapshot;
        }
    }

    private Snapshot Build()
    {
        var albums = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.MusicAlbum }, Recursive = true })
            .OfType<MusicAlbum>()
            .Select(a => (Album: a, Key: AlbumKey(a.Name), Marked: IsMarked(a)))
            .Where(a => a.Marked && a.Key.Length >= 3)
            .ToList();

        var titles = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false
            })
            .Where(t => t is Movie or Series)
            .ToList();

        // title key -> titles, so each album needs only a few lookups
        var byKey = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);
        foreach (var t in titles)
        {
            foreach (var key in new[] { TitleKeys.Key(t.Name), TitleKeys.Key(t.OriginalTitle) }.Where(k => k.Length >= 3).Distinct())
            {
                if (!byKey.TryGetValue(key, out var list))
                {
                    byKey[key] = list = new List<BaseItem>();
                }

                if (!list.Contains(t))
                {
                    list.Add(t);
                }
            }
        }

        var titleToAlbums = new Dictionary<Guid, List<Guid>>();
        var albumToTitles = new Dictionary<Guid, List<Guid>>();
        foreach (var (album, key, _) in albums)
        {
            foreach (var candidateKey in Candidates(key))
            {
                if (!byKey.TryGetValue(candidateKey, out var matches))
                {
                    continue;
                }

                foreach (var t in matches.Where(t => YearsFit(t, album)))
                {
                    AddTo(titleToAlbums, t.Id, album.Id);
                    AddTo(albumToTitles, album.Id, t.Id);
                }

                break; // the exact name wins over the one without a volume number
            }
        }

        // several albums for one title: the one whose name is closest (shortest) first, then by year
        var albumById = albums.ToDictionary(a => a.Album.Id, a => a.Album);
        foreach (var list in titleToAlbums.Values)
        {
            list.Sort((x, y) =>
            {
                var c = albumById[x].Name.Length.CompareTo(albumById[y].Name.Length);
                return c != 0 ? c : (albumById[x].ProductionYear ?? 0).CompareTo(albumById[y].ProductionYear ?? 0);
            });
        }

        return new Snapshot(DateTime.UtcNow, titleToAlbums, albumToTitles);

        static void AddTo(Dictionary<Guid, List<Guid>> map, Guid key, Guid value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = new List<Guid>();
            }

            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }
    }

    /// <summary>"stranger things vol 1" -> "stranger things vol 1", "stranger things" (volume, part or season dropped).</summary>
    private static IEnumerable<string> Candidates(string key)
    {
        yield return key;
        var shorter = VolumeTail().Replace(key, string.Empty).Trim();
        if (shorter.Length >= 3 && shorter != key)
        {
            yield return shorter;
        }
    }

    internal static string AlbumKey(string? albumName)
    {
        var s = albumName ?? string.Empty;
        s = Bracketed().Replace(s, " ");
        s = TrailingDecoration().Replace(s, string.Empty);
        s = VolumeSuffix().Replace(s, string.Empty);
        return TitleKeys.Key(s);
    }

    internal static bool IsMarked(MusicAlbum album)
    {
        return SoundtrackWords().IsMatch(album.Name ?? string.Empty)
            || album.Genres.Any(g => SoundtrackGenres.Contains(g.Trim()));
    }

    private static bool YearsFit(BaseItem title, MusicAlbum album)
    {
        var albumYear = album.ProductionYear ?? album.PremiereDate?.Year;
        var titleYear = title.ProductionYear ?? title.PremiereDate?.Year;
        if (albumYear is null || titleYear is null)
        {
            return true;
        }

        if (title is Series series)
        {
            var end = series.EndDate?.Year ?? DateTime.UtcNow.Year;
            return albumYear >= titleYear - 1 && albumYear <= end + 2;
        }

        // soundtracks appear around the release; re-releases and anniversary editions are named as such
        return albumYear >= titleYear - 1 && albumYear <= titleYear + 2;
    }

    private sealed record Snapshot(DateTime Built, Dictionary<Guid, List<Guid>> TitleToAlbums, Dictionary<Guid, List<Guid>> AlbumToTitles);

    [GeneratedRegex(@"(?i)\b(?:o\.?s\.?t\.?|soundtracks?|original\s+(?:motion\s+picture\s+|television\s+|series\s+|film\s+)?(?:score|soundtrack|music)|music\s+(?:from|inspired\s+by)|motion\s+picture|film\s*musik|filmmusik|musique\s+(?:du\s+film|originale)|bande\s+originale|score)\b|サウンドトラック|オリジナル")]
    private static partial Regex SoundtrackWords();

    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"(?:\s(?:vol|part|season|staffel|series|teil|chapter|book))?\s\d{1,2}$")]
    private static partial Regex VolumeTail();

    [GeneratedRegex(@"(?i)\s*[-–—:]\s*(?:(?:the\s+)?original\b.*|o\.?s\.?t\.?\b.*|music\s+from\b.*|(?:the\s+)?(?:complete\s+)?score\b.*|soundtrack\b.*|filmmusik\b.*)$")]
    private static partial Regex TrailingDecoration();

    [GeneratedRegex(@"(?i)\s*\b(?:o\.?s\.?t\.?|soundtrack|original\s+(?:motion\s+picture\s+)?(?:score|soundtrack))\s*$")]
    private static partial Regex VolumeSuffix();
}
