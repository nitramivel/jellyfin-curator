using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// Publishes Curator's categories as home screen rows — by registering them
    /// with Home Screen Sections directly, or by writing them into Collection
    /// Sections' configuration — then sets their order and card shape and,
    /// optionally, enables them for target users through Modular Home settings.
    /// </summary>
    public interface IHomeScreenIntegrationService
    {
        /// <summary>
        /// Whether each prerequisite plugin is currently loaded.
        /// </summary>
        /// <remarks>
        /// Exposed for the health check. Both integrations degrade silently by
        /// design — a missing one logs and returns false rather than throwing — so
        /// from the outside an uninstalled prerequisite looks exactly like Curator
        /// having stopped working. This is how that gets named instead of guessed at.
        /// </remarks>
        /// <returns>Loaded state for Collection Sections and Home Screen Sections.</returns>
        (bool CollectionSections, bool HomeScreenSections) GetPrerequisites();

        /// <summary>
        /// Syncs the home screen so exactly the given categories appear as rows.
        /// Degrades gracefully: if the prerequisite plugins are missing or their
        /// endpoints fail, this logs clearly and returns rather than throwing —
        /// playlists are still built either way.
        /// </summary>
        /// <param name="categories">The categories that currently have playlists.</param>
        /// <param name="targetUserIds">Users to enable sections for when auto-enable is on.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Whether rows were published, and whether by the configured route.</returns>
        Task<SectionSyncResult> SyncSectionsAsync(
            IReadOnlyList<CategoryDefinition> categories,
            IReadOnlyList<Guid> targetUserIds,
            CancellationToken cancellationToken);

        /// <summary>
        /// Registers just the context rows, and sets their order, shape and per-user
        /// enablement.
        /// </summary>
        /// <remarks>
        /// Narrow on purpose. These rows are re-registered whenever the weather or
        /// the hour turns over — several times a day — and putting that through
        /// <see cref="SyncSectionsAsync"/> would re-register fifty category rows and
        /// rewrite every viewer's enabled list each time, to change two titles.
        /// <para>
        /// Registration is keyed on the section ID with last-write-wins, and order
        /// and shape live in Home Screen Sections' own configuration rather than in
        /// the registration — so re-registering a row to rename it disturbs nothing
        /// else about it.
        /// </para>
        /// </remarks>
        /// <param name="rows">The context rows to publish.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Whether the rows were registered.</returns>
        Task<bool> SyncContextRowsAsync(
            IReadOnlyList<ContextRowRegistration> rows,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// One context row ready to publish: the section, what it shows, and who may see
    /// it.
    /// </summary>
    /// <param name="Request">The registration payload.</param>
    /// <param name="Audience">
    /// The viewers this row is enabled for. One viewer for a per-viewer row, every
    /// target user for a shared one — this is what stops a per-viewer row appearing
    /// on somebody else's home screen with a title describing weather they are not
    /// standing in.
    /// </param>
    public sealed record ContextRowRegistration(
        SectionRegistrationRequest Request,
        IReadOnlyList<Guid> Audience);
}
