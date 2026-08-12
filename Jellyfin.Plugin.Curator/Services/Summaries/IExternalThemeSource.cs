using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Curator.Services.Summaries
{
    /// <summary>
    /// Mood and tone descriptions another plugin has already paid a model for.
    ///
    /// <para>
    /// The condensing pass asks a model two things it cannot get from a metadata
    /// overview: rewrite this so the <em>feel</em> survives, and say what weather
    /// and hour it suits. An overview is a poor input for both — it describes the
    /// premise, and premise is exactly what neither question is about.
    /// </para>
    ///
    /// <para>
    /// Concierge, the search plugin, already buys precisely that judgement for its
    /// own index: its <c>Themes</c> field is documented as "subject and tone — what
    /// it is about and what watching it feels like", and on the library this was
    /// built against it holds phrases like <i>lonely and heartbreaking</i>,
    /// <i>stylish and unsettling</i>, <i>warm and hopeful</i>. That is a better
    /// answer to "what sky does this suit" than any synopsis, and it has already
    /// been paid for.
    /// </para>
    ///
    /// <para>
    /// Strictly an <b>additional</b> input, never a replacement. Every item still
    /// goes to the model with its own overview; themes ride alongside when they
    /// exist. An install without the other plugin, or with an unreadable file, gets
    /// exactly the behaviour it had before — see hard rule 21 on why a soft,
    /// fail-open read is the only acceptable shape for this.
    /// </para>
    /// </summary>
    public interface IExternalThemeSource
    {
        /// <summary>
        /// Themes by item ID, for every item another plugin has described.
        /// </summary>
        /// <remarks>
        /// Returns empty rather than throwing for every failure there is: nothing
        /// installed, no file, a file written by a version whose shape changed, a
        /// permissions problem. None of those are Curator's business to report as
        /// errors, and none may cost a pass that would otherwise run.
        /// </remarks>
        /// <returns>Themes by item, empty when there is nothing to read.</returns>
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> GetThemes();
    }
}
