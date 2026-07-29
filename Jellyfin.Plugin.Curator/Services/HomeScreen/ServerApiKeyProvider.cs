using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Security;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// Default <see cref="IApiKeyProvider"/>: reuses (or creates once) an API key
    /// named "Curator" via the server's authentication manager. The key is visible
    /// to admins under Dashboard → API Keys and can be revoked there.
    /// </summary>
    public class ServerApiKeyProvider : IApiKeyProvider
    {
        private const string KeyName = "Curator";

        private readonly IAuthenticationManager _authenticationManager;
        private string? _cachedToken;

        public ServerApiKeyProvider(IAuthenticationManager authenticationManager)
        {
            _authenticationManager = authenticationManager;
        }

        /// <inheritdoc />
        public async Task<string> GetTokenAsync()
        {
            if (_cachedToken is not null)
            {
                return _cachedToken;
            }

            var keys = await _authenticationManager.GetApiKeys().ConfigureAwait(false);
            var existing = keys.FirstOrDefault(k => string.Equals(k.AppName, KeyName, StringComparison.Ordinal));
            if (existing is null)
            {
                await _authenticationManager.CreateApiKey(KeyName).ConfigureAwait(false);
                keys = await _authenticationManager.GetApiKeys().ConfigureAwait(false);
                existing = keys.FirstOrDefault(k => string.Equals(k.AppName, KeyName, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("Curator: failed to create the Curator API key.");
            }

            _cachedToken = existing.AccessToken;
            return _cachedToken;
        }
    }
}
