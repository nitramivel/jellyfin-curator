using System.Threading.Tasks;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// Supplies an API token for Curator's calls to the server's own HTTP API
    /// (the Collection Sections config endpoint and the Modular Home user
    /// settings endpoint are both authorized routes).
    /// </summary>
    public interface IApiKeyProvider
    {
        /// <summary>
        /// Gets the access token, creating Curator's named API key on first use.
        /// </summary>
        /// <returns>The token.</returns>
        Task<string> GetTokenAsync();
    }
}
