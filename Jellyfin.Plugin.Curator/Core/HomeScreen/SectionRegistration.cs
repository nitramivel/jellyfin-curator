using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Curator.Core.HomeScreen
{
    /// <summary>
    /// Builds the payload that registers one Curator row directly with Home Screen
    /// Sections, cutting Collection Sections out of the path.
    ///
    /// <para>
    /// Home Screen Sections takes a registration describing where to get a row's
    /// contents from, and offers two ways of saying it: an HTTP endpoint, or an
    /// assembly / class / method triple it reflects into. This uses the triple.
    /// The endpoint form builds its own <c>HttpClient</c> with no credentials, so
    /// it can only ever call something anonymous, and Curator's controller is
    /// admin-only for good reason. The triple is also what Collection Sections
    /// itself uses, which makes it the path with a working example on this server.
    /// </para>
    ///
    /// <para>
    /// Nothing here references a Home Screen Sections type. The registration is a
    /// JSON object handed over by reflection, so Curator carries no compile-time
    /// dependency on a plugin that may not be installed — see
    /// <c>Services/HomeScreen/HomeScreenSectionRegistrar</c> for the hand-over, and
    /// hard rule 21 for why the dependency is worth avoiding.
    /// </para>
    /// </summary>
    public static class SectionRegistration
    {
        /// <summary>
        /// The method Home Screen Sections calls for a row's contents.
        /// </summary>
        /// <remarks>
        /// Resolved with <c>Type.GetMethod(name)</c> on the other side, which throws
        /// on an overload — so the class named in a registration must declare
        /// exactly one public method with this name.
        /// </remarks>
        public const string ResultsMethodName = "GetResults";

        /// <summary>
        /// Builds one section registration.
        /// </summary>
        /// <remarks>
        /// Keys are camelCase to match the registration Collection Sections sends,
        /// which is the shape known to work against this plugin.
        /// <para>
        /// <c>additionalData</c> carries the category ID, and it is the only thing
        /// the results call receives besides the viewer. It is echoed back by the
        /// client rather than remembered by the server, so it is untrusted input on
        /// the way in and the handler validates it — but it is also what lets a row
        /// name its category by GUID instead of by the playlist name Collection
        /// Sections had to match on. That is hard rule 3 finally holding all the way
        /// to the screen: six viewers share one category name and each has their own
        /// playlist, so a name cannot say which one a row means.
        /// </para>
        /// </remarks>
        /// <param name="section">The row to register.</param>
        /// <param name="categoryId">The category whose playlist the row shows.</param>
        /// <param name="resultsAssembly">Full name of the assembly holding the results class.</param>
        /// <param name="resultsClass">Full name of the results class.</param>
        /// <returns>The registration as a JSON object.</returns>
        public static string BuildPayload(
            DesiredSection section,
            Guid categoryId,
            string resultsAssembly,
            string resultsClass)
        {
            ArgumentNullException.ThrowIfNull(section);
            ArgumentException.ThrowIfNullOrEmpty(resultsAssembly);
            ArgumentException.ThrowIfNullOrEmpty(resultsClass);

            var payload = new JsonObject
            {
                ["id"] = section.SectionId,
                ["displayText"] = section.Name,

                // Instance count, not item count. Home Screen Sections asks a
                // section for several instances of itself when this is above one —
                // that is how "Because You Watched" becomes three rows. A category
                // is one row.
                ["limit"] = 1,
                ["additionalData"] = categoryId.ToString("N"),
                ["resultsAssembly"] = resultsAssembly,
                ["resultsClass"] = resultsClass,
                ["resultsMethod"] = ResultsMethodName,
            };

            return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
    }
}
