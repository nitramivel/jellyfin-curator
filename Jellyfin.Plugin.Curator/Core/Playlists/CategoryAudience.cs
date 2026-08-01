using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Curator.Core.Playlists
{
    /// <summary>
    /// Who a category's playlists belong to.
    ///
    /// <para>
    /// One line of logic, and getting it wrong put every viewer on every row. A
    /// shared category goes to everyone targeted; a personal one goes to the single
    /// viewer it was invented for and to nobody else. The run knew that — it passed
    /// <c>[userId]</c> when it built a personal category — but the reconcile pass
    /// walked every stored definition and passed the full target list to all of
    /// them, so each nightly cleanup handed one viewer's private rows to the whole
    /// household. Measured: 102 definitions, 80 of them personal, every one holding
    /// a playlist for all six users.
    /// </para>
    ///
    /// <para>
    /// Pure, because it is the kind of thing that reads as obviously correct at
    /// every call site and is only wrong at one of them.
    /// </para>
    /// </summary>
    public static class CategoryAudience
    {
        /// <summary>
        /// The users a category's playlists should exist for.
        /// </summary>
        /// <remarks>
        /// A personal category whose owner is no longer targeted resolves to nobody
        /// rather than to everybody. That is the case that made the original bug
        /// dangerous: the natural fallback for "this category has an owner I cannot
        /// place" is the full list, and the full list is exactly wrong.
        /// </remarks>
        /// <param name="ownerUserId">The category's owner, or null when it is shared.</param>
        /// <param name="targetUsers">The users this run is building playlists for.</param>
        /// <returns>The audience, which may be empty.</returns>
        public static IReadOnlyList<Guid> For(Guid? ownerUserId, IReadOnlyList<Guid> targetUsers)
        {
            ArgumentNullException.ThrowIfNull(targetUsers);

            if (ownerUserId is not { } owner || owner == Guid.Empty)
            {
                return targetUsers;
            }

            return targetUsers.Contains(owner) ? [owner] : [];
        }
    }
}
