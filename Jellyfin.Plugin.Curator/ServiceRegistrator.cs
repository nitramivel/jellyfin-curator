using Jellyfin.Plugin.Curator.Services;
using Jellyfin.Plugin.Curator.Services.Categories;
using Jellyfin.Plugin.Curator.Services.Context;
using Jellyfin.Plugin.Curator.Services.Health;
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
            serviceCollection.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();
            serviceCollection.AddSingleton<CategoryProposalService>();
            serviceCollection.AddSingleton<ICategoryStore, CategoryStore>();
            serviceCollection.AddSingleton<ICuratorPlaylistService, CuratorPlaylistService>();
            serviceCollection.AddSingleton<IApiKeyProvider, ServerApiKeyProvider>();

            // A singleton because its whole value is the cache it holds: coordinates
            // for the life of the process, conditions for half an hour. A transient
            // one would geocode on every home screen load.
            serviceCollection.AddSingleton<IWeatherService, OpenMeteoWeatherService>();
            serviceCollection.AddSingleton<IContextRowStore, ContextRowStore>();
            serviceCollection.AddSingleton<ContextRowService>();
            serviceCollection.AddSingleton<ISectionRegistrar, HomeScreenSectionRegistrar>();
            serviceCollection.AddSingleton<IHomeScreenIntegrationService, HomeScreenIntegrationService>();

            // CuratorSectionResults is deliberately absent. Home Screen Sections
            // constructs it itself with ActivatorUtilities, which builds an
            // unregistered type as long as every constructor argument resolves from
            // this container — so what matters is that its dependencies are
            // registered here, which they are, and registering the type as well
            // would only suggest something in Curator resolves it.
            serviceCollection.AddSingleton<IRunLogStore, RunLogStore>();
            serviceCollection.AddSingleton<ISummaryStore, SummaryStore>();
            serviceCollection.AddSingleton<SummaryDistillService>();
            serviceCollection.AddSingleton<HealthService>();
            serviceCollection.AddSingleton<CuratorRunService>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, GenerateCategoriesTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, DistillSummariesTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, MaintenanceTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, HealthCheckTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, RefreshRecommendationsTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, PublishHomeScreenRowsTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, RefreshContextRowsTask>();
        }
    }
}
