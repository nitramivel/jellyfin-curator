using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Curator.Core.Models
{
    /// <summary>
    /// One category's per-user playlist link. Jellyfin playlists are user-scoped,
    /// so one category maps to N playlists across N users. Playlists are tracked
    /// by GUID only — never resolved by name.
    /// </summary>
    public sealed class UserPlaylistLink
    {
        /// <summary>Gets or sets the Jellyfin user this link belongs to.</summary>
        public required Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets the Jellyfin playlist ID, or null while the category is
        /// empty (playlist removed, definition kept, recreated on repopulation).
        /// </summary>
        public Guid? PlaylistId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the user removed Curator's
        /// ownership tag from this playlist. A handed-off playlist is theirs
        /// permanently: never modified, never deleted, never replaced.
        /// </summary>
        public bool HandedOff { get; set; }
    }

    /// <summary>
    /// The persisted definition of one category — stored as an individual JSON
    /// file in the plugin data directory, not in the plugin config XML.
    /// </summary>
    public sealed class CategoryDefinition
    {
        /// <summary>Gets or sets Curator's stable internal ID for this category.</summary>
        public required Guid Id { get; set; }

        /// <summary>Gets or sets the category name (also the playlist name — the Collection Sections join key).</summary>
        public required string Name { get; set; }

        /// <summary>Gets or sets the one-line description.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Gets or sets the member item IDs from the latest run, confidence order.</summary>
        public List<Guid> Members { get; set; } = [];

        /// <summary>Gets or sets how many batch proposals produced this category in the latest run.</summary>
        public int SourceProposalCount { get; set; }

        /// <summary>Gets or sets when the definition was first created (UTC).</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>Gets or sets when the definition was last updated by a run (UTC).</summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>Gets or sets the model identifier that last produced this category.</summary>
        public string ModelId { get; set; } = string.Empty;

        /// <summary>Gets or sets the per-user playlist links.</summary>
        public List<UserPlaylistLink> UserPlaylists { get; set; } = [];

        /// <summary>
        /// Gets the link for a user, creating it if absent.
        /// </summary>
        /// <param name="userId">The user.</param>
        /// <returns>The link.</returns>
        public UserPlaylistLink GetOrAddLink(Guid userId)
        {
            var link = UserPlaylists.Find(l => l.UserId == userId);
            if (link is null)
            {
                link = new UserPlaylistLink { UserId = userId };
                UserPlaylists.Add(link);
            }

            return link;
        }
    }
}
