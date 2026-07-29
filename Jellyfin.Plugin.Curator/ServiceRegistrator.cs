using Jellyfin.Plugin.Curator.Services.Library;
using Jellyfin.Plugin.Curator.Services.Llm;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Curator
{
    /// <summary>
    /// Registers Curator's services with Jellyfin's DI container.
    /// </summary>
    public sealed class ServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<ILibraryScanner, LibraryScanner>();
            serviceCollection.AddSingleton<IUserActivityProvider, UserActivityProvider>();
            serviceCollection.AddSingleton<LlmProviderFactory>();
            serviceCollection.AddSingleton<CategoryProposalService>();
        }
    }
}
