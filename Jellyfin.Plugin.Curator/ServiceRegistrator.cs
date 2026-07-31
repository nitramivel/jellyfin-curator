using Jellyfin.Plugin.Curator.Services;
using Jellyfin.Plugin.Curator.Services.Categories;
using Jellyfin.Plugin.Curator.Services.HomeScreen;
using Jellyfin.Plugin.Curator.Services.Library;
using Jellyfin.Plugin.Curator.Services.Llm;
using Jellyfin.Plugin.Curator.Services.Playlists;
using Jellyfin.Plugin.Curator.Services.Runs;
using Jellyfin.Plugin.Curator.Services.Summaries;
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
            serviceCollection.AddSingleton<ICategoryStore, CategoryStore>();
            serviceCollection.AddSingleton<ICuratorPlaylistService, CuratorPlaylistService>();
            serviceCollection.AddSingleton<IApiKeyProvider, ServerApiKeyProvider>();
            serviceCollection.AddSingleton<IHomeScreenIntegrationService, HomeScreenIntegrationService>();
            serviceCollection.AddSingleton<IRunLogStore, RunLogStore>();
            serviceCollection.AddSingleton<ISummaryStore, SummaryStore>();
            serviceCollection.AddSingleton<SummaryDistillService>();
            serviceCollection.AddSingleton<CuratorRunService>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, GenerateCategoriesTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, DistillSummariesTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, MaintenanceTask>();
        }
    }
}
