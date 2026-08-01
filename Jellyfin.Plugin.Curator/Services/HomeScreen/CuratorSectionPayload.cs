using System;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// What Home Screen Sections passes when it asks Curator for a row's contents.
    /// </summary>
    /// <remarks>
    /// Structurally identical to that plugin's own payload type and deliberately
    /// so: it serializes its payload to JSON and deserializes it into whatever type
    /// our results method declares, so this is a copy rather than a reference and
    /// Curator keeps its independence from an assembly that may not be installed.
    /// Both fields arrive from the client's own request, so neither is trusted —
    /// see <see cref="CuratorSectionResults"/>.
    /// </remarks>
    public sealed class CuratorSectionPayload
    {
        /// <summary>Gets or sets the viewer whose home screen is being drawn.</summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets the category ID this row shows, as registered.
        /// </summary>
        public string? AdditionalData { get; set; }
    }
}
