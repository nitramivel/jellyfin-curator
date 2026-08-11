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
    /// <param name="CollectionSectionsRequired">
    /// Whether rows go through Collection Sections at all. False once Curator
    /// serves its own rows, which makes that plugin optional — reporting it as
    /// missing then would be the check crying wolf, which rule 19 forbids.
    /// </param>
    /// <param name="GhostItems">Library rows sitting outside every configured library folder.</param>
    /// <param name="LibraryItems">Real items the last scan found.</param>
    /// <param name="UseCondensedSummaries">Whether runs are sending condensed summaries.</param>
    /// <param name="ItemsMissingSummary">Items with a long overview and no condensed summary.</param>
    /// <param name="RecommendationsEnabled">Whether recommendation playlists are switched on.</param>
    /// <param name="TargetUserCount">How many users the plugin builds for.</param>
    /// <param name="RecommendationPlaylistCount">How many of them actually have one.</param>
    /// <param name="CategoriesWithoutPlaylist">Stored definitions currently showing nobody anything.</param>
    /// <param name="TotalCategories">Stored definitions in all.</param>
    /// <param name="PublishRowsRunsAtStartup">
    /// Whether Publish Home Screen Rows still carries its startup trigger. Defaults
    /// to true — healthy — so a caller that does not gather it never fires the
    /// finding.
    /// </param>
    /// <param name="ContextRowsEnabled">Whether the weather and time-of-day rows are switched on.</param>
    /// <param name="ItemsWithContext">
    /// Items the condensing pass has actually judged for when they suit watching.
    /// </param>
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
        int TotalCategories = 0,
        bool ConsolidateTags = false,
        int StoredSummaries = 0,
        int SummariesWithTags = 0,
        int LastSummaryPassDistilled = 0,
        int LastSummaryPassFailed = 0,
        bool CollectionSectionsRequired = true,
        bool PublishRowsRunsAtStartup = true,
        bool ContextRowsEnabled = false,
        int ItemsWithContext = 0);

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
            CheckTagConsolidation(facts, findings);
            CheckSummaryFailures(facts, findings);
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
            // Only complain about Collection Sections when rows actually go
            // through it. Curator serving its own rows makes it an escape hatch
            // rather than a prerequisite, and a Problem raised over an uninstalled
            // plugin nothing depends on is exactly the noise that gets the whole
            // panel ignored.
            if (facts.CollectionSectionsRequired && !facts.CollectionSectionsLoaded)
            {
                findings.Add(new HealthFinding(
                    "integration.collectionsections",
                    HealthSeverity.Problem,
                    "The Collection Sections plugin is not loaded",
                    "Playlists are still built, but nothing publishes them as home screen rows. Either install or "
                    + "re-enable it, or set the home screen row source to Curator, then press Re-sync home screen rows."));
            }

            if (!facts.HomeScreenSectionsLoaded)
            {
                findings.Add(new HealthFinding(
                    "integration.homescreensections",
                    HealthSeverity.Problem,
                    "The Home Screen Sections plugin is not loaded",
                    "Rows cannot appear, be ordered, or be enabled per user without it — it is required whichever "
                    + "row source is set. Install or re-enable it, then press Re-sync home screen rows."));
            }

            // A section registered with Home Screen Sections lives in memory and is
            // never written down (rule 22), so the startup trigger on Publish Home
            // Screen Rows is the only thing that brings the rows back after a
            // restart. Losing it is silent and total: playlists stay perfectly
            // healthy, every row disappears, and nothing anywhere says why. That is
            // this panel's entire remit.
            //
            // Only when Curator owns its rows. Under the Collection Sections path
            // that plugin re-registers them from its own config on its own startup
            // task, so the trigger is not load-bearing and saying otherwise would be
            // the crying wolf rule 19 forbids.
            if (!facts.CollectionSectionsRequired && !facts.PublishRowsRunsAtStartup)
            {
                findings.Add(new HealthFinding(
                    "homescreen.nostartuptrigger",
                    HealthSeverity.Problem,
                    "Publish Home Screen Rows no longer runs at server start",
                    "Curator's rows are registered in memory and do not survive a restart, so this task is what "
                    + "brings them back — without it every Curator row will be absent after the next restart, "
                    + "however healthy the playlists behind them are. Add an \"On application startup\" trigger to "
                    + "it under Dashboard → Scheduled Tasks, then run it once to restore the rows now."));
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

        /// <summary>
        /// Tag consolidation switched on and producing nothing.
        /// </summary>
        /// <remarks>
        /// This shipped broken and ran for weeks. The response schema forbade the
        /// very field the prompt asked for, so the model had nowhere legal to put the
        /// tags and every item came back with an empty list — no error, no warning,
        /// nothing in any log. The cost was paid on every pass. A check this blunt
        /// would have caught it the first day: a feature that is on, has run, and has
        /// produced zero output across the whole library is not a subtle judgement.
        /// <para>
        /// Deliberately requires a decent sample. A handful of items whose scraped
        /// tags were all production trivia genuinely produce nothing, and that is a
        /// correct answer rather than a fault.
        /// </para>
        /// </remarks>
        private static void CheckTagConsolidation(HealthFacts facts, List<HealthFinding> findings)
        {
            const int EnoughToJudge = 20;

            if (!facts.ConsolidateTags
                || facts.StoredSummaries < EnoughToJudge
                || facts.SummariesWithTags > 0)
            {
                return;
            }

            findings.Add(new HealthFinding(
                "summaries.notags",
                HealthSeverity.Warning,
                "Tag consolidation is on but no item has any tags",
                $"All {facts.StoredSummaries} stored summaries came back with an empty tag list. Consolidation "
                + "costs part of every distillation call whether or not it produces anything, so this is spend "
                + "buying nothing. It usually means the model is not answering in the shape the pass expects — "
                + "check a recent summaries run in the Runs tab and look at what actually came back."));
        }

        /// <summary>
        /// A distillation pass that lost most of what it was paid for.
        /// </summary>
        /// <remarks>
        /// Measured: one pass distilled 27 items of 212 and the other 185 were
        /// written off after every one of them had been paid for. The only outward
        /// sign was a summary count that stopped climbing.
        /// </remarks>
        private static void CheckSummaryFailures(HealthFacts facts, List<HealthFinding> findings)
        {
            var attempted = facts.LastSummaryPassDistilled + facts.LastSummaryPassFailed;
            if (attempted == 0 || facts.LastSummaryPassFailed * 2 <= attempted)
            {
                return;
            }

            findings.Add(new HealthFinding(
                "summaries.failing",
                HealthSeverity.Warning,
                $"The last distillation pass failed {facts.LastSummaryPassFailed} of {attempted} items",
                "Most of that pass was paid for and thrown away. The usual cause is the response being cut off "
                + "by the output cap — thinking counts against it, so a profile set to think on a batch this "
                + "size will hit it. Lower the batch size, raise Max output tokens, or point the pass at a "
                + "profile that does not think."));
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

            // Two rows switched on that nothing can fill. This is the purest shape of
            // what this panel is for: everything involved is working, no log line
            // names a cause, and the only symptom is two rows that never appear —
            // because the setting that publishes them and the setting that buys their
            // contents are deliberately separate, and only one has been turned on.
            if (facts.ContextRowsEnabled && facts.ItemsWithContext == 0)
            {
                findings.Add(new HealthFinding(
                    "context.unclassified",
                    HealthSeverity.Warning,
                    "The weather and time-of-day rows are on, but nothing has been judged for them yet",
                    "Those rows are filled from a judgement the Condense Summaries pass makes. Switch on "
                    + "\"Judge when an item suits watching\" on the Summaries tab and run Condense now; until "
                    + "something is classified, both rows stay empty and are not drawn."));
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
