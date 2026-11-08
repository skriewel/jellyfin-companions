using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Companions.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the sections are shown in the web client.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether soundtracks from the music libraries are linked.</summary>
    public bool ShowSoundtracks { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether books and audiobooks from the book servers are linked.</summary>
    public bool ShowBooks { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether book servers are only asked for titles that are adaptations
    /// (a source author such as "Novel" in the credits, or a "based on …" keyword).
    /// </summary>
    public bool OnlyAdaptations { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the English title is looked up through Jellyfin's TMDB provider
    /// and searched as well (books and manga are often on the server under it).
    /// </summary>
    public bool UseEnglishTitle { get; set; } = true;

    /// <summary>Gets or sets how long book lookups are cached, in hours.</summary>
    public int CacheHours { get; set; } = 12;

    /// <summary>Gets or sets the book servers.</summary>
    public List<BookServerConfig> BookServers { get; set; } = new();

    /// <summary>Gets or sets a value that changes on every save (part of the client cache key).</summary>
    public long Revision { get; set; }
}

/// <summary>One Audiobookshelf, Kavita, Grimmory, BookOrbit or other OPDS server.</summary>
public class BookServerConfig
{
    /// <summary>Gets or sets a stable id (used for cover requests).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the kind: Audiobookshelf, Kavita, Grimmory, BookOrbit or Opds.</summary>
    public string Kind { get; set; } = "Audiobookshelf";

    /// <summary>Gets or sets the name shown to users ("Kavita", "Hörbücher" …). Empty: the kind.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the address the Jellyfin server uses to reach the book server.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the address browsers use for links. Empty: same as <see cref="Url"/>.</summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the API token (Audiobookshelf) or auth key (Kavita).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the OPDS user (Grimmory, BookOrbit, other OPDS).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the OPDS password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the OPDS catalog path for "Opds" servers (Grimmory and BookOrbit use /api/v1/opds).</summary>
    public string OpdsPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the web page of a book for "Opds" servers, with {id} for the book id.</summary>
    public string LinkTemplate { get; set; } = string.Empty;
}
