using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Curator.Core.Health
{
    /// <summary>How much a finding matters.</summary>
    public enum HealthSeverity
    {
        /// <summary>Working as intended.</summary>
        Ok = 0,

        /// <summary>Degraded, or heading that way. Worth acting on, not urgent.</summary>
        Warning = 1,

        /// <summary>Something the owner is paying for is not happening.</summary>
        Problem = 2,
    }

    /// <summary>One thing the check has to say.</summary>
    /// <param name="Id">Stable identifier, so the page can style a finding without matching on prose.</param>
    /// <param name="Severity">How much it matters.</param>
    /// <param name="Title">The finding, in one line.</param>
    /// <param name="Detail">What to do about it.</param>
    public sealed record HealthFinding(string Id, HealthSeverity Severity, string Title, string Detail);

    /// <summary>
    /// Everything the check needs, gathered by the service layer so the judgement
    /// itself stays pure.
    /// </summary>
    /// <param name="UtcNow">The current time.</param>
    /// <param name="LastSuccessfulRun">When a run last completed, or null if none ever has.</param>
    /// <param name="LastRunStatus">How the most recent run ended.</param>
    /// <param name="LastRunError">Why it failed, when it did.</param>
    /// <param name="ExpectedRunIntervalHours">How often the category task is scheduled; null when manual-only.</param>
    /// <param name="ModelProfileCount">Saved model profiles.</param>
    /// <param name="ProfilesMissingKey">Profiles whose provider needs an API key and has none.</param>
    /// <param name="CollectionSectionsLoaded">Whether the Collection Sections plugin is loaded.</param>
    /// <param name="HomeScreenSectionsLoaded">Whether the Home Screen Sections plugin is loaded.</param>
    /// <param name="GhostItems">Library rows sitting outside every configured library folder.</param>
    /// <param name="LibraryItems">Real items the last scan found.</param>
    /// <param name="UseCondensedSummaries">Whether runs are sending condensed summaries.</param>
    /// <param name="ItemsMissingSummary">Items with a long overview and no condensed summary.</param>
    /// <param name="RecommendationsEnabled">Whether recommendation playlists are switched on.</param>
    /// <param name="TargetUserCount">How many users the plugin builds for.</param>
    /// <param name="RecommendationPlaylistCount">How many of them actually have one.</param>
    /// <param name="CategoriesWithoutPlaylist">Stored definitions currently showing nobody anything.</param>
    /// <param name="TotalCategories">Stored definitions in all.</param>
    public sealed record HealthFacts(
        DateTime UtcNow,
        DateTime? LastSuccessfulRun = null,
        string? LastRunStatus = null,
        string? LastRunError = null,
        double? ExpectedRunIntervalHours = null,
        int ModelProfileCount = 0,
        int ProfilesMissingKey = 0,
        bool CollectionSectionsLoaded = true,
        bool HomeScreenSectionsLoaded = true,
        int GhostItems = 0,
        int LibraryItems = 0,
        bool UseCondensedSummaries = false,
        int ItemsMissingSummary = 0,
        bool RecommendationsEnabled = false,
        int TargetUserCount = 0,
        int RecommendationPlaylistCount = 0,
        int CategoriesWithoutPlaylist = 0,
        int TotalCategories = 0);

    /// <summary>
    /// Looks for the ways Curator goes quietly wrong.
    ///
    /// <para>
    /// Every failure this catches has actually happened on a real server. A run
    /// dies mid-flight because installing any plugin tears the host down and
    /// rebuilds it. A prerequisite plugin gets uninstalled and its rows simply stop
    /// appearing. Library rows outlive the folder they came from and go to the
    /// model as unplayable items — 36 of 304 on a measured library, for weeks,
    /// noticed by nobody. None of these throw, and all of them look like "the
    /// plugin stopped doing anything" from the outside.
    /// </para>
    /// <para>
    /// Pure, so the judgements are testable without a server. The service layer
    /// gathers the facts; this decides what they mean.
    /// </para>
    /// </summary>
    public static class HealthCheck
    {
        /// <summary>
        /// How many scheduled intervals may pass with no successful run before it
        /// counts as stopped rather than merely late.
        /// </summary>
        /// <remarks>
        /// Two and a half rather than one: a task that fired an hour late, or a run
        /// that was skipped because another was in progress, is normal. Crying about
        /// it teaches the owner to ignore this panel, which is worse than not having
        /// it.
        /// </remarks>
        private const double MissedIntervalTolerance = 2.5;

        /// <summary>
        /// Evaluates the facts.
        /// </summary>
        /// <param name="facts">What the service layer found.</param>
        /// <returns>Findings, most severe first; empty when everything is fine.</returns>
        public static IReadOnlyList<HealthFinding> Evaluate(HealthFacts facts)
        {
            ArgumentNullException.ThrowIfNull(facts);

            var findings = new List<HealthFinding>();

            CheckModel(facts, findings);
            CheckRuns(facts, findings);
            CheckIntegrations(facts, findings);
            CheckLibrary(facts, findings);
            CheckSummaries(facts, findings);
            CheckOutputs(facts, findings);

            findings.Sort((a, b) => b.Severity.CompareTo(a.Severity));
            return findings;
        }

        private static void CheckModel(HealthFacts facts, List<HealthFinding> findings)
        {
            if (facts.ModelProfileCount == 0)
            {
                findings.Add(new HealthFinding(
                    "model.none",
                    HealthSeverity.Problem,
                    "No model profile is configured",
                    "Nothing can run until one exists. Add a profile on the Model tab."));
                return;
            }

            if (facts.ProfilesMissingKey > 0)
            {
                findings.Add(new HealthFinding(
                    "model.nokey",
                    HealthSeverity.Warning,
                    string.Create(CultureInfo.InvariantCulture, $"{facts.ProfilesMissingKey} model profile(s) have no API key"),
                    "A run using one of those will fail at the first call. Add the key, or remove the profile "
                    + "if you no longer use it."));
            }
        }

        private static void CheckRuns(HealthFacts facts, List<HealthFinding> findings)
        {
            if (facts.LastSuccessfulRun is null)
            {
                findings.Add(new HealthFinding(
                    "run.never",
                    HealthSeverity.Warning,
                    "No run has ever completed",
                    "Categories, playlists and rows are all produced by a run. Start one from the Runs tab."));
                return;
            }

            if (string.Equals(facts.LastRunStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new HealthFinding(
                    "run.failed",
                    HealthSeverity.Problem,
                    "The most recent run failed",
                    string.IsNullOrWhiteSpace(facts.LastRunError)
                        ? "See the Runs tab for what happened."
                        : facts.LastRunError));
            }

            // Only meaningful when the task is actually on a timer. A deliberately
            // manual-only schedule is not a fault, and reporting it as one would be
            // nagging about a choice the owner made.
            if (facts.ExpectedRunIntervalHours is not { } interval || interval <= 0)
            {
                return;
            }

            var age = facts.UtcNow - facts.LastSuccessfulRun.Value;
            if (age.TotalHours > interval * MissedIntervalTolerance)
            {
                findings.Add(new HealthFinding(
                    "run.stalled",
                    HealthSeverity.Problem,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"No successful run for {age.TotalDays:F1} days, but one is scheduled every {interval / 24:F1}"),
                    "Scheduled runs appear to have stopped. Check the Runs tab for a failure, and the Schedule "
                    + "tab for whether the task still has a trigger."));
            }
        }

        private static void CheckIntegrations(HealthFacts facts, List<HealthFinding> findings)
        {
            if (!facts.CollectionSectionsLoaded)
            {
                findings.Add(new HealthFinding(
                    "integration.collectionsections",
                    HealthSeverity.Problem,
                    "The Collection Sections plugin is not loaded",
                    "Playlists are still built, but nothing publishes them as home screen rows. Install or "
                    + "re-enable it, then press Re-sync home screen rows."));
            }

            if (!facts.HomeScreenSectionsLoaded)
            {
                findings.Add(new HealthFinding(
                    "integration.homescreensections",
                    HealthSeverity.Problem,
                    "The Home Screen Sections plugin is not loaded",
                    "Rows cannot be ordered or enabled per user without it. Install or re-enable it, then press "
                    + "Re-sync home screen rows."));
            }
        }

        private static void CheckLibrary(HealthFacts facts, List<HealthFinding> findings)
        {
            if (facts.GhostItems <= 0)
            {
                return;
            }

            var share = facts.LibraryItems + facts.GhostItems > 0
                ? 100.0 * facts.GhostItems / (facts.LibraryItems + facts.GhostItems)
                : 0;

            findings.Add(new HealthFinding(
                "library.ghosts",
                HealthSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{facts.GhostItems} library row(s) point at a folder that no longer exists ({share:F0}% of the library)"),
                "These are left behind when a library folder is removed or a mount is renamed. Curator keeps them "
                + "away from the model, but they still show in Jellyfin and play back as nothing. A library scan "
                + "in Jellyfin clears them."));
        }

        private static void CheckSummaries(HealthFacts facts, List<HealthFinding> findings)
        {
            if (!facts.UseCondensedSummaries || facts.ItemsMissingSummary <= 0)
            {
                return;
            }

            findings.Add(new HealthFinding(
                "summaries.incomplete",
                HealthSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{facts.ItemsMissingSummary} item(s) have no condensed summary, but runs are sending them"),
                "Those items fall back to their full overview, so the library is being described two different "
                + "ways in the same prompt. Press Condense now on the Summaries tab."));
        }

        private static void CheckOutputs(HealthFacts facts, List<HealthFinding> findings)
        {
            if (facts.RecommendationsEnabled
                && facts.TargetUserCount > 0
                && facts.RecommendationPlaylistCount < facts.TargetUserCount)
            {
                findings.Add(new HealthFinding(
                    "recommendations.missing",
                    HealthSeverity.Warning,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{facts.TargetUserCount - facts.RecommendationPlaylistCount} of {facts.TargetUserCount} viewers have no recommendation playlist"),
                    "They are built at the end of a run and refreshed by the daily cleanup. If this persists, "
                    + "those viewers may have no categories yet."));
            }

            // Only worth mentioning when it is most of the list. A few spent
            // definitions are the normal cost of the model rephrasing itself.
            if (facts.TotalCategories > 0
                && facts.CategoriesWithoutPlaylist * 2 > facts.TotalCategories)
            {
                findings.Add(new HealthFinding(
                    "categories.empty",
                    HealthSeverity.Warning,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{facts.CategoriesWithoutPlaylist} of {facts.TotalCategories} categories hold no playlist"),
                    "Definitions are kept after their rows go so a returning category keeps its identity, but "
                    + "this many suggests runs are failing part-way or the caps are far below what the model "
                    + "proposes. Clean up and sync will clear the ones that are truly spent."));
            }
        }
    }
}
