using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.HomeScreen;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// One row to register: what it looks like, what it is about, and who answers
    /// for its contents.
    /// </summary>
    /// <param name="Section">The row's ID, name and size.</param>
    /// <param name="AdditionalData">
    /// The one string the results call receives besides the viewer. A category row
    /// carries its category GUID; a context row carries which of the two it is. It
    /// is echoed back by the client rather than remembered by the server, so every
    /// handler treats it as untrusted input.
    /// </param>
    /// <param name="ResultsType">
    /// The class Home Screen Sections reflects into for this row's contents. Passed
    /// as a type rather than a name so the compiler keeps the registration and the
    /// handler in step — a renamed class is a build error here instead of an empty
    /// row on somebody's home screen.
    /// </param>
    public sealed record SectionRegistrationRequest(
        DesiredSection Section,
        string AdditionalData,
        Type ResultsType);

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
        /// <param name="sections">The sections to register, with what each one shows.</param>
        /// <returns>How many registered, or null when Home Screen Sections could not be reached at all.</returns>
        int? RegisterSections(IReadOnlyList<SectionRegistrationRequest> sections);
    }
}
