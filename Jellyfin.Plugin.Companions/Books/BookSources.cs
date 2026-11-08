using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.Companions.Configuration;

namespace Jellyfin.Plugin.Companions.Books;

/// <summary>A book, audiobook or comic found on a book server.</summary>
public sealed record BookHit(
    string ServerId,
    string ExternalId,
    string Title,
    IReadOnlyList<string> Authors,
    string Kind,
    string? Url,
    string? CoverUrl);

/// <summary>A book server could not be asked.</summary>
public sealed class BookSourceException : Exception
{
    public BookSourceException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Searches one book server.</summary>
public abstract partial class BookSource
{
    protected BookSource(BookServerConfig config, HttpClient http)
    {
        Config = config;
        Http = http;
    }

    public BookServerConfig Config { get; }

    protected HttpClient Http { get; }

    protected string BaseUrl => Config.Url.Trim().TrimEnd('/');

    protected string PublicBase => (string.IsNullOrWhiteSpace(Config.PublicUrl) ? Config.Url : Config.PublicUrl).Trim().TrimEnd('/');

    public static BookSource Create(BookServerConfig config, HttpClient http) => config.Kind switch
    {
        "Audiobookshelf" => new AudiobookshelfSource(config, http),
        "Kavita" => new KavitaSource(config, http),
        _ => new OpdsSource(config, http)
    };

    /// <summary>Books whose title or author matches the query.</summary>
    public abstract Task<IReadOnlyList<BookHit>> SearchAsync(string query, CancellationToken ct);

    /// <summary>Adds the server's credentials to a request (also used for cover requests).</summary>
    public abstract void Authorize(HttpRequestMessage request);

    /// <summary>One cheap request that proves address and credentials work; returns a short description.</summary>
    public abstract Task<string> TestAsync(CancellationToken ct);

    protected async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(req);
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BookSourceException("timeout", $"{Label}: no answer from {BaseUrl}");
        }
        catch (HttpRequestException ex)
        {
            throw new BookSourceException("network", $"{Label}: {ex.Message}");
        }

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            resp.Dispose();
            throw new BookSourceException("auth", $"{Label}: access denied ({(int)resp.StatusCode}), check the key or user");
        }

        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            resp.Dispose();
            throw new BookSourceException("http", $"{Label}: HTTP {code} for {url}");
        }

        return resp;
    }

    protected async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(url, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new BookSourceException("format", $"{Label}: {url} did not return JSON");
        }
    }

    protected string Label => string.IsNullOrWhiteSpace(Config.Name) ? Config.Kind : Config.Name;

    protected static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var p in e.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    _ => null
                };
            }
        }

        return null;
    }

    protected static JsonElement Prop(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value;
                }
            }
        }

        return default;
    }

    protected static IEnumerable<JsonElement> Arr(JsonElement e, string name)
    {
        var a = Prop(e, name);
        return a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }
}

/// <summary>Audiobookshelf: GET /api/libraries, GET /api/libraries/{id}/search?q= (Bearer API token).</summary>
public sealed class AudiobookshelfSource : BookSource
{
    private List<string>? _libraries;

    public AudiobookshelfSource(BookServerConfig config, HttpClient http)
        : base(config, http)
    {
    }

    public override void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(Config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey.Trim());
        }
    }

    public override async Task<string> TestAsync(CancellationToken ct)
    {
        _libraries = null;
        var libs = await LibrariesAsync(ct).ConfigureAwait(false);
        return $"{libs.Count} book librar{(libs.Count == 1 ? "y" : "ies")}";
    }

    public override async Task<IReadOnlyList<BookHit>> SearchAsync(string query, CancellationToken ct)
    {
        var hits = new List<BookHit>();
        var authorIds = new HashSet<string>();
        foreach (var lib in await LibrariesAsync(ct).ConfigureAwait(false))
        {
            var json = await GetJsonAsync($"{BaseUrl}/api/libraries/{Uri.EscapeDataString(lib)}/search?q={Uri.EscapeDataString(query)}&limit=12", ct).ConfigureAwait(false);
            foreach (var match in Arr(json, "book"))
            {
                if (Parse(Prop(match, "libraryItem")) is { } hit)
                {
                    hits.Add(hit);
                }
            }

            // the search finds books by title; books by an author come from the author's page
            foreach (var author in Arr(json, "authors"))
            {
                if (Str(author, "id") is { Length: > 0 } aid
                    && (Matching.TitleKeys.SameAuthor(Str(author, "name"), query) || Matching.TitleKeys.Surname(Str(author, "name")) == Matching.TitleKeys.Key(query)))
                {
                    authorIds.Add(aid);
                }
            }
        }

        foreach (var aid in authorIds.Take(3))
        {
            var author = await GetJsonAsync($"{BaseUrl}/api/authors/{Uri.EscapeDataString(aid)}?include=items", ct).ConfigureAwait(false);
            var name = Str(author, "name");
            foreach (var item in Arr(author, "libraryItems"))
            {
                if (Parse(item, name) is { } hit)
                {
                    hits.Add(hit);
                }
            }
        }

        return hits;
    }

    private BookHit? Parse(JsonElement item, string? knownAuthor = null)
    {
        var id = Str(item, "id");
        var media = Prop(item, "media");
        var meta = Prop(media, "metadata");
        var title = Str(meta, "title");
        if (id is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var authors = Arr(meta, "authors").Select(a => Str(a, "name")).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
        if (authors.Count == 0 && Str(meta, "authorName") is { Length: > 0 } an)
        {
            authors.AddRange(an.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (authors.Count == 0 && !string.IsNullOrWhiteSpace(knownAuthor))
        {
            authors.Add(knownAuthor);
        }

        var hasAudio = Arr(media, "audioFiles").Any() || Arr(media, "tracks").Any()
            || (int.TryParse(Str(media, "numAudioFiles"), out var n) && n > 0)
            || (int.TryParse(Str(media, "numTracks"), out var n2) && n2 > 0);
        var hasEbook = Prop(media, "ebookFile").ValueKind == JsonValueKind.Object || !string.IsNullOrEmpty(Str(media, "ebookFormat"));
        var kind = hasAudio || !hasEbook ? "audiobook" : "ebook";

        return new BookHit(Config.Id, id, title!, authors, kind, $"{PublicBase}/item/{Uri.EscapeDataString(id)}", $"{BaseUrl}/api/items/{Uri.EscapeDataString(id)}/cover?width=300");
    }

    private async Task<List<string>> LibrariesAsync(CancellationToken ct)
    {
        if (_libraries is not null)
        {
            return _libraries;
        }

        var json = await GetJsonAsync($"{BaseUrl}/api/libraries", ct).ConfigureAwait(false);
        _libraries = Arr(json, "libraries")
            .Where(l => !string.Equals(Str(l, "mediaType"), "podcast", StringComparison.OrdinalIgnoreCase))
            .Select(l => Str(l, "id"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
        return _libraries;
    }
}

/// <summary>Kavita: GET /api/Search/search?queryString= and /api/Series/metadata?seriesId= (x-api-key).</summary>
public sealed class KavitaSource : BookSource
{
    public KavitaSource(BookServerConfig config, HttpClient http)
        : base(config, http)
    {
    }

    public override void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(Config.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", Config.ApiKey.Trim());
        }
    }

    public override async Task<string> TestAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync($"{BaseUrl}/api/Library/libraries", ct).ConfigureAwait(false);
        var n = json.ValueKind == JsonValueKind.Array ? json.GetArrayLength() : 0;
        return $"{n} librar{(n == 1 ? "y" : "ies")}";
    }

    public override async Task<IReadOnlyList<BookHit>> SearchAsync(string query, CancellationToken ct)
    {
        var json = await GetJsonAsync($"{BaseUrl}/api/Search/search?queryString={Uri.EscapeDataString(query)}&includeChapterAndFiles=false", ct).ConfigureAwait(false);
        var hits = new List<BookHit>();
        foreach (var s in Arr(json, "series").Take(12))
        {
            var id = Str(s, "seriesId");
            var lib = Str(s, "libraryId");
            var title = Str(s, "name");
            if (id is null || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var authors = new List<string>();
            try
            {
                var meta = await GetJsonAsync($"{BaseUrl}/api/Series/metadata?seriesId={Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
                authors.AddRange(Arr(meta, "writers").Select(w => Str(w, "name")).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!));
            }
            catch (BookSourceException)
            {
                // the title alone still counts when it is unambiguous
            }

            // MangaFormat: 0 image, 1 archive, 2 unknown, 3 epub, 4 pdf
            var kind = Str(s, "format") is "0" or "1" ? "comic" : "ebook";
            var localized = Str(s, "localizedName");
            var original = Str(s, "originalName");
            hits.Add(new BookHit(Config.Id, id, title!, authors, kind, $"{PublicBase}/library/{lib}/series/{id}", $"{BaseUrl}/api/Image/series-cover?seriesId={Uri.EscapeDataString(id)}"));
            foreach (var alt in new[] { localized, original }.Where(a => !string.IsNullOrWhiteSpace(a) && a != title).Distinct())
            {
                hits.Add(new BookHit(Config.Id, id, alt!, authors, kind, $"{PublicBase}/library/{lib}/series/{id}", $"{BaseUrl}/api/Image/series-cover?seriesId={Uri.EscapeDataString(id)}"));
            }
        }

        return hits;
    }
}

/// <summary>
/// OPDS 1.2 catalogs with OpenSearch: Grimmory and BookOrbit (/api/v1/opds, web page /book/{id}) and any
/// other OPDS server. Basic authentication with an OPDS user.
/// </summary>
public sealed partial class OpdsSource : BookSource
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";
    private string? _template;

    public OpdsSource(BookServerConfig config, HttpClient http)
        : base(config, http)
    {
    }

    private string CatalogUrl
    {
        get
        {
            var path = Config.Kind is "Grimmory" or "BookOrbit" ? "/api/v1/opds" : Config.OpdsPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return BaseUrl;
            }

            return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : BaseUrl + "/" + path.Trim().TrimStart('/');
        }
    }

    private string LinkTemplate => Config.Kind is "Grimmory" or "BookOrbit"
        ? "/book/{id}"
        : Config.LinkTemplate;

    public override void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(Config.Username))
        {
            var raw = Encoding.UTF8.GetBytes(Config.Username + ":" + Config.Password);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }
    }

    public override async Task<string> TestAsync(CancellationToken ct)
    {
        _template = null;
        var template = await SearchTemplateAsync(ct).ConfigureAwait(false);
        return "search: " + template;
    }

    public override async Task<IReadOnlyList<BookHit>> SearchAsync(string query, CancellationToken ct)
    {
        var template = await SearchTemplateAsync(ct).ConfigureAwait(false);
        var url = template
            .Replace("{searchTerms}", Uri.EscapeDataString(query), StringComparison.Ordinal)
            .Replace("{startPage?}", "1", StringComparison.Ordinal)
            .Replace("{startIndex?}", "0", StringComparison.Ordinal)
            .Replace("{count?}", "25", StringComparison.Ordinal);
        url = OptionalParams().Replace(url, string.Empty);

        var feed = await GetXmlAsync(url, ct).ConfigureAwait(false);
        var hits = new List<BookHit>();
        foreach (var entry in feed.Root?.Elements(Atom + "entry") ?? Enumerable.Empty<XElement>())
        {
            var title = entry.Element(Atom + "title")?.Value?.Trim();
            var rawId = entry.Element(Atom + "id")?.Value?.Trim();
            var links = entry.Elements(Atom + "link").ToList();
            var isBook = links.Any(l => ((string?)l.Attribute("rel") ?? string.Empty).StartsWith("http://opds-spec.org/acquisition", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(rawId) || !isBook)
            {
                continue; // navigation entries (authors, series …) are not books
            }

            var authors = entry.Elements(Atom + "author").Select(a => a.Element(Atom + "name")?.Value?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            var types = links.Select(l => (string?)l.Attribute("type") ?? string.Empty).ToList();
            var kind = types.Any(t => t.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || t.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
                ? "audiobook"
                : types.Any(t => t.Contains("cbz", StringComparison.OrdinalIgnoreCase) || t.Contains("cbr", StringComparison.OrdinalIgnoreCase) || t.Contains("comicbook", StringComparison.OrdinalIgnoreCase))
                    ? "comic"
                    : "ebook";

            var cover = links.FirstOrDefault(l => (string?)l.Attribute("rel") == "http://opds-spec.org/image/thumbnail")
                ?? links.FirstOrDefault(l => (string?)l.Attribute("rel") == "http://opds-spec.org/image");
            var coverUrl = cover is null ? null : Resolve(url, (string?)cover.Attribute("href"));

            // "urn:booklore:book:42", "urn:bookorbit:book:42" -> "42"
            var idMatch = TrailingId().Match(rawId);
            var id = idMatch.Success ? idMatch.Groups[1].Value : rawId;
            string? web = null;
            if (!string.IsNullOrWhiteSpace(LinkTemplate))
            {
                var path = LinkTemplate.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);
                web = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : PublicBase + "/" + path.TrimStart('/');
            }

            hits.Add(new BookHit(Config.Id, id, title!, authors, kind, web, coverUrl));
        }

        return hits;
    }

    private async Task<string> SearchTemplateAsync(CancellationToken ct)
    {
        if (_template is not null)
        {
            return _template;
        }

        var root = await GetXmlAsync(CatalogUrl, ct).ConfigureAwait(false);
        var links = root.Root?.Elements(Atom + "link").Where(l => (string?)l.Attribute("rel") == "search").ToList() ?? new List<XElement>();

        // a direct Atom template ("…?q={searchTerms}") or an OpenSearch description that holds it
        var direct = links.FirstOrDefault(l => ((string?)l.Attribute("href") ?? string.Empty).Contains("{searchTerms}", StringComparison.Ordinal));
        if (direct is not null)
        {
            return _template = Resolve(CatalogUrl, (string?)direct.Attribute("href"))!;
        }

        var desc = links.FirstOrDefault(l => ((string?)l.Attribute("type") ?? string.Empty).Contains("opensearchdescription", StringComparison.OrdinalIgnoreCase))
            ?? links.FirstOrDefault();
        if (desc is null)
        {
            throw new BookSourceException("format", $"{Label}: the OPDS catalog at {CatalogUrl} offers no search");
        }

        var descUrl = Resolve(CatalogUrl, (string?)desc.Attribute("href"))!;
        var os = await GetXmlAsync(descUrl, ct).ConfigureAwait(false);
        var urls = os.Root?.Elements().Where(e => e.Name.LocalName == "Url").ToList() ?? new List<XElement>();
        var best = urls.FirstOrDefault(u => ((string?)u.Attribute("type") ?? string.Empty).Contains("atom", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault();
        var template = (string?)best?.Attribute("template");
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new BookSourceException("format", $"{Label}: the OpenSearch description at {descUrl} has no search template");
        }

        return _template = Resolve(descUrl, template)!;
    }

    private async Task<XDocument> GetXmlAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(url, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        }
        catch (System.Xml.XmlException)
        {
            throw new BookSourceException("format", $"{Label}: {url} is not an OPDS feed");
        }
    }

    private static string? Resolve(string baseUrl, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        // keep "{searchTerms}" intact: Uri would escape the braces
        var placeholder = href.Replace("{", "%7B", StringComparison.Ordinal).Replace("}", "%7D", StringComparison.Ordinal);
        var resolved = new Uri(new Uri(baseUrl), placeholder).ToString();
        return resolved.Replace("%7B", "{", StringComparison.OrdinalIgnoreCase).Replace("%7D", "}", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"[:/]([^:/]+)$")]
    private static partial Regex TrailingId();

    [GeneratedRegex(@"[&?][A-Za-z_:]+=\{[^}]*\?\}")]
    private static partial Regex OptionalParams();
}
