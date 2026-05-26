using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AutoChapterSkip
{
    /// <summary>
    /// Registers the background services for the plugin.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // This is the magic line that forces Jellyfin to actually boot our code!
            serviceCollection.AddHostedService<AutoChapterSkip>();
        }
    }
}
