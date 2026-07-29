using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// Publishes Curator's categories as home screen rows by writing into
    /// Collection Sections' configuration and, optionally, enabling the
    /// resulting sections for target users through Modular Home settings.
    /// </summary>
    public interface IHomeScreenIntegrationService
    {
        /// <summary>
        /// Syncs the home screen so exactly the given categories appear as rows.
        /// Degrades gracefully: if the prerequisite plugins are missing or their
        /// endpoints fail, this logs clearly and returns rather than throwing —
        /// playlists are still built either way.
        /// </summary>
        /// <param name="categories">The categories that currently have playlists.</param>
        /// <param name="targetUserIds">Users to enable sections for when auto-enable is on.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True when the home screen was updated; false when integration was unavailable.</returns>
        Task<bool> SyncSectionsAsync(
            IReadOnlyList<CategoryDefinition> categories,
            IReadOnlyList<Guid> targetUserIds,
            CancellationToken cancellationToken);
    }
}
