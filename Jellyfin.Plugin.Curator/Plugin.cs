using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Curator
{
    /// <summary>
    /// The Curator plugin: LLM-inferred vibe categories surfaced as home screen rows.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override Guid Id => Guid.Parse("de2b72e7-90f9-47e8-aeef-0436d71d01ac");

        public override string Name => "Curator";

        public override string Description =>
            "Sends your library to an LLM, asks what threads run through it, and builds the answers into ordered playlists surfaced as home screen rows.";

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return
            [
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                },
            ];
        }
    }
}
