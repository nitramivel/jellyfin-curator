using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.HomeScreen;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// Registers Curator's rows directly with Home Screen Sections.
    /// </summary>
    public interface ISectionRegistrar
    {
        /// <summary>
        /// Registers every given section, replacing any previous registration under
        /// the same ID.
        /// </summary>
        /// <remarks>
        /// Registration is in-memory on the other side and does not persist, so this
        /// has to run on every server start and not only after a run. It is also
        /// additive there: nothing removes a section, so a row that should no longer
        /// exist is stopped by being dropped from the section settings and from each
        /// viewer's enabled list, not by being unregistered.
        /// </remarks>
        /// <param name="sections">The sections to register, with the category each shows.</param>
        /// <returns>How many registered, or null when Home Screen Sections could not be reached at all.</returns>
        int? RegisterSections(IReadOnlyList<(DesiredSection Section, Guid CategoryId)> sections);
    }
}
