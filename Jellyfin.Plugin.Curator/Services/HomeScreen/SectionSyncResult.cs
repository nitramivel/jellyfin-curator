namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// What a home screen sync achieved.
    /// </summary>
    /// <remarks>
    /// Two facts rather than one, because "the rows are up" and "the rows are up
    /// the way you asked for" have different answers and different remedies. A sync
    /// that could not register directly falls back to Collection Sections and
    /// publishes rows that work — so reporting only success would be true and
    /// useless, and the startup task would stop retrying having settled for the
    /// path the owner switched away from.
    /// </remarks>
    /// <param name="Published">Whether rows reached the home screen at all.</param>
    /// <param name="Degraded">
    /// Whether they got there by a route other than the configured one. Only ever
    /// true when the integrated path was asked for and could not be used, which is
    /// usually the other plugin not having finished starting — so it is worth
    /// trying again.
    /// </param>
    public sealed record SectionSyncResult(bool Published, bool Degraded)
    {
        /// <summary>Nothing was published, and retrying will not obviously help.</summary>
        public static SectionSyncResult Failed { get; } = new(false, false);

        /// <summary>Published exactly as configured.</summary>
        public static SectionSyncResult Ok { get; } = new(true, false);
    }
}
