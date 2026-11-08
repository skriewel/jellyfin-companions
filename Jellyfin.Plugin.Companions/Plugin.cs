using Jellyfin.Plugin.Companions.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Companions;

/// <summary>
/// Companions: soundtracks, books and audiobooks that belong to a movie or series.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    /// <summary>Raised after the configuration was saved.</summary>
    public static event EventHandler? ConfigurationSaved;

    public override string Name => "Companions";

    public override Guid Id => Guid.Parse("5d0a3a1e-2e48-4358-b9a5-5942845aedb4");

    public override string Description =>
        "Links movies and series to their soundtrack in your music library and to the book or audiobook in Audiobookshelf, Kavita, Grimmory or BookOrbit.";

    /// <summary>Gets a key that changes with the plugin version, the client assets and every save.</summary>
    public string CacheKey => $"{Version}-{AssetHash.Value}-{Configuration.Revision}";

    private static readonly Lazy<string> AssetHash = new(() =>
    {
        var asm = typeof(Plugin).Assembly;
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var name in new[] { "Web.companions.js", "Web.companions.css" })
        {
            using var s = asm.GetManifestResourceStream($"{typeof(Plugin).Namespace}.{name}");
            if (s is null)
            {
                continue;
            }

            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!)[..10].ToLowerInvariant();
    });

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration cfg)
        {
            cfg.Revision = DateTime.UtcNow.Ticks;
        }

        base.UpdateConfiguration(configuration);
        ConfigurationSaved?.Invoke(this, EventArgs.Empty);
    }
}
