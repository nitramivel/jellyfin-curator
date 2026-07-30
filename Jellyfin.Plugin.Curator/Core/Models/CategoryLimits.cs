using System;

namespace Jellyfin.Plugin.Curator.Core.Models
{
    /// <summary>
    /// The limits on one pool of categories: how small a category may be, how
    /// large it may grow, and how many of them survive.
    ///
    /// <para>
    /// This type exists to make one specific bug impossible. Every limit here is
    /// enforced on the model's answer <em>and</em> stated in the prompt that asks
    /// for it, and those were previously separate arguments passed from separate
    /// places — so they drifted, twice, in opposite directions:
    /// </para>
    /// <list type="bullet">
    /// <item>The prompt asked for 3-member categories while the filter demanded 6.
    /// A measured run had 17 of 22 proposals binned on size alone.</item>
    /// <item>The filter allowed 8 categories while the prompt named no target at
    /// all. One model read that as "be exhaustive" and returned 23 covering 78%
    /// of the library; another read it as "satisfy the constraint" and returned 5
    /// covering 10%.</item>
    /// </list>
    /// <para>
    /// A limit the model is not told is a limit it cannot aim at, and a limit
    /// stated but not enforced is a lie. Both consumers — <see cref="Llm.PromptBuilder"/>
    /// and <see cref="Reconciliation.Reconciler"/> — now take this one value, so
    /// there is no second number to keep in step. Pass the same instance to both;
    /// do not unpack it into loose integers on the way.
    /// </para>
    /// </summary>
    /// <param name="MinMembers">Smallest category kept. Clamped up to 2 — a one-member category is not a category.</param>
    /// <param name="MaxMembers">Largest category kept; the excess is trimmed off the tail. 0 means no limit.</param>
    /// <param name="MaxCategories">How many categories survive. 0 means no cap.</param>
    public sealed record CategoryLimits(int MinMembers, int MaxMembers = 0, int MaxCategories = 0)
    {
        /// <summary>
        /// The smallest floor worth asking for. Two is the least that can express
        /// a thread between items.
        /// </summary>
        public const int MinimumMembersFloor = 2;

        /// <summary>
        /// Gets the member floor actually applied, whatever was configured.
        /// </summary>
        public int EffectiveMinMembers => Math.Max(MinimumMembersFloor, MinMembers);

        /// <summary>
        /// Gets the member ceiling actually applied, or 0 when there is none.
        /// </summary>
        /// <remarks>
        /// A ceiling at or below the floor is discarded rather than honoured: it
        /// would ask the model for "between 6 and 4 members" and leave the
        /// Reconciler trimming every category to below the size it just demanded,
        /// which empties the run. The floor is the load-bearing number, so it wins.
        /// </remarks>
        public int EffectiveMaxMembers => MaxMembers > EffectiveMinMembers ? MaxMembers : 0;

        /// <summary>Gets a value indicating whether a category count cap applies.</summary>
        public bool HasCategoryCap => MaxCategories > 0;
    }
}
