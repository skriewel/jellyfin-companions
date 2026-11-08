using Jellyfin.Plugin.Companions.Books;
using Jellyfin.Plugin.Companions.Injection;
using Jellyfin.Plugin.Companions.Soundtracks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Companions;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
        serviceCollection.AddSingleton<SoundtrackIndex>();
        serviceCollection.AddSingleton<BookService>();
    }
}
