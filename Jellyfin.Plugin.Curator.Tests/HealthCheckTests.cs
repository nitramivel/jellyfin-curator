using System;
using System.Linq;
using Jellyfin.Plugin.Curator.Core.Health;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The health check's judgements.
    ///
    /// Every case here is a way this plugin has actually gone wrong quietly on a
    /// real server: a run dying mid-flight when installing any plugin tore the host
    /// down, a prerequisite plugin being uninstalled so rows simply stopped
    /// appearing, library rows outliving the folder they came from. None of those
    /// throw, so the only thing standing between them and going unnoticed for weeks
    /// is this.
    ///
    /// The other half of the job is not crying wolf. A panel that reports normal
    /// operation as a problem teaches the owner to ignore it, which is worse than
    /// having no panel — so the quiet cases are pinned just as hard.
    /// </summary>
    public class HealthCheckTests
    {
        private static readonly DateTime Now = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A healthy server: everything configured, everything current.</summary>
        private static HealthFacts Healthy() => new(
            UtcNow: Now,
            LastSuccessfulRun: Now.AddDays(-1),
            LastRunStatus: "completed",
            ExpectedRunIntervalHours: 24 * 7,
            ModelProfileCount: 1,
            CollectionSectionsLoaded: true,
            HomeScreenSectionsLoaded: true,
            LibraryItems: 263,
            TargetUserCount: 5,
            RecommendationPlaylistCount: 5,
            TotalCategories: 20,
            CategoriesWithoutPlaylist: 2);

        private static bool Has(HealthFacts facts, string id)
            => HealthCheck.Evaluate(facts).Any(f => string.Equals(f.Id, id, StringComparison.Ordinal));

        private static HealthFinding Find(HealthFacts facts, string id)
            => HealthCheck.Evaluate(facts).Single(f => string.Equals(f.Id, id, StringComparison.Ordinal));

        [Fact]
        public void AHealthyServerProducesNoFindings()
        {
            Assert.Empty(HealthCheck.Evaluate(Healthy()));
        }

        [Fact]
        public void FindingsComeBackMostSevereFirst()
        {
            // The page shows these in order and the first line is what gets read.
            var facts = Healthy() with
            {
                CollectionSectionsLoaded = false,
                GhostItems = 36,
            };

            var findings = HealthCheck.Evaluate(facts);

            Assert.Equal(HealthSeverity.Problem, findings[0].Severity);
            Assert.True(findings.Count > 1);
        }

        // ---- runs that stopped happening ----

        [Fact]
        public void RunsThatStoppedAreAProblem()
        {
            var facts = Healthy() with { LastSuccessfulRun = Now.AddDays(-30) };

            Assert.Equal(HealthSeverity.Problem, Find(facts, "run.stalled").Severity);
        }

        [Fact]
        public void ARunThatIsMerelyLateIsNotReported()
        {
            // A task that fired late, or a run skipped because another was going, is
            // normal. Crying about it is how a panel gets ignored.
            var facts = Healthy() with { LastSuccessfulRun = Now.AddDays(-9) };

            Assert.False(Has(facts, "run.stalled"));
        }

        [Fact]
        public void AManualOnlyScheduleIsNeverReportedAsStalled()
        {
            // Running only when asked is a choice, not a fault.
            var facts = Healthy() with
            {
                LastSuccessfulRun = Now.AddDays(-90),
                ExpectedRunIntervalHours = null,
            };

            Assert.False(Has(facts, "run.stalled"));
        }

        [Fact]
        public void AFailedRunIsReportedWithItsReason()
        {
            var facts = Healthy() with
            {
                LastRunStatus = "failed",
                LastRunError = "401 Unauthorized",
            };

            Assert.Contains("401", Find(facts, "run.failed").Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void NeverHavingRunIsAWarningNotAProblem()
        {
            // A fresh install has not done anything wrong.
            var facts = Healthy() with { LastSuccessfulRun = null, LastRunStatus = null };

            Assert.Equal(HealthSeverity.Warning, Find(facts, "run.never").Severity);
            Assert.False(Has(facts, "run.stalled"));
        }

        // ---- configuration ----

        [Fact]
        public void NoModelProfileIsAProblem()
        {
            Assert.Equal(HealthSeverity.Problem, Find(Healthy() with { ModelProfileCount = 0 }, "model.none").Severity);
        }

        [Fact]
        public void NoModelProfileSuppressesTheMissingKeyNoise()
        {
            // One clear instruction beats two overlapping ones.
            var facts = Healthy() with { ModelProfileCount = 0, ProfilesMissingKey = 3 };

            Assert.False(Has(facts, "model.nokey"));
        }

        [Fact]
        public void AProfileWithoutAKeyIsAWarning()
        {
            Assert.Equal(
                HealthSeverity.Warning,
                Find(Healthy() with { ProfilesMissingKey = 1 }, "model.nokey").Severity);
        }

        // ---- integrations ----

        [Theory]
        [InlineData(false, true, "integration.collectionsections")]
        [InlineData(true, false, "integration.homescreensections")]
        public void AMissingPrerequisitePluginIsAProblem(bool collection, bool homeScreen, string id)
        {
            // Both integrations degrade silently by design, so from the outside an
            // uninstalled prerequisite is indistinguishable from Curator being broken.
            var facts = Healthy() with
            {
                CollectionSectionsLoaded = collection,
                HomeScreenSectionsLoaded = homeScreen,
            };

            Assert.Equal(HealthSeverity.Problem, Find(facts, id).Severity);
        }

        [Fact]
        public void CollectionSectionsMissingIsNotReportedWhenNothingGoesThroughIt()
        {
            // Curator serving its own rows makes that plugin an escape hatch, not a
            // prerequisite. A Problem raised over an uninstalled plugin nothing
            // depends on is the noise that gets the whole panel ignored.
            var facts = Healthy() with
            {
                CollectionSectionsLoaded = false,
                CollectionSectionsRequired = false,
            };

            Assert.DoesNotContain(HealthCheck.Evaluate(facts), f => f.Id == "integration.collectionsections");
        }

        [Fact]
        public void HomeScreenSectionsMissingIsReportedWhicheverRowSourceIsSet()
        {
            // Owning the row removes one dependency, not both.
            var facts = Healthy() with
            {
                HomeScreenSectionsLoaded = false,
                CollectionSectionsRequired = false,
            };

            Assert.Equal(HealthSeverity.Problem, Find(facts, "integration.homescreensections").Severity);
        }

        // ---- library ----

        [Fact]
        public void GhostRowsAreReportedWithTheirShareOfTheLibrary()
        {
            // The real numbers from a measured server, unnoticed for weeks.
            var facts = Healthy() with { GhostItems = 36, LibraryItems = 268 };

            var finding = Find(facts, "library.ghosts");

            Assert.Equal(HealthSeverity.Warning, finding.Severity);
            Assert.Contains("36", finding.Title, StringComparison.Ordinal);
            Assert.Contains("12%", finding.Title, StringComparison.Ordinal);
            Assert.Contains("library scan", finding.Detail, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ACleanLibrarySaysNothing()
        {
            Assert.False(Has(Healthy() with { GhostItems = 0 }, "library.ghosts"));
        }

        // ---- summaries ----

        [Fact]
        public void AnIncompleteSummaryCacheIsOnlyReportedWhenItIsBeingUsed()
        {
            // Half-built and unused is the normal state while you are still building
            // it. Half-built and switched on describes the library two ways at once.
            var building = Healthy() with { UseCondensedSummaries = false, ItemsMissingSummary = 100 };
            var inUse = Healthy() with { UseCondensedSummaries = true, ItemsMissingSummary = 100 };

            Assert.False(Has(building, "summaries.incomplete"));
            Assert.True(Has(inUse, "summaries.incomplete"));
        }

        // ---- outputs ----

        [Fact]
        public void ViewersWithoutARecommendationPlaylistAreReported()
        {
            var facts = Healthy() with
            {
                RecommendationsEnabled = true,
                TargetUserCount = 5,
                RecommendationPlaylistCount = 2,
            };

            Assert.Contains("3 of 5", Find(facts, "recommendations.missing").Title, StringComparison.Ordinal);
        }

        [Fact]
        public void RecommendationsSwitchedOffAreNotMissed()
        {
            var facts = Healthy() with
            {
                RecommendationsEnabled = false,
                TargetUserCount = 5,
                RecommendationPlaylistCount = 0,
            };

            Assert.False(Has(facts, "recommendations.missing"));
        }

        [Fact]
        public void EmptyCategoriesAreOnlyReportedWhenTheyAreMostOfTheList()
        {
            // A few spent definitions are the normal cost of the model rephrasing
            // itself between runs, and kept on purpose so a returning category keeps
            // its identity.
            var few = Healthy() with { TotalCategories = 20, CategoriesWithoutPlaylist = 5 };
            var most = Healthy() with { TotalCategories = 20, CategoriesWithoutPlaylist = 15 };

            Assert.False(Has(few, "categories.empty"));
            Assert.True(Has(most, "categories.empty"));
        }

        [Fact]
        public void NoCategoriesAtAllDoesNotDivideByZero()
        {
            var facts = Healthy() with { TotalCategories = 0, CategoriesWithoutPlaylist = 0 };

            Assert.False(Has(facts, "categories.empty"));
        }

        [Fact]
        public void EveryFindingSaysWhatToDoAboutIt()
        {
            // A finding with no remedy is just an alarm.
            var facts = Healthy() with
            {
                LastSuccessfulRun = null,
                ModelProfileCount = 0,
                CollectionSectionsLoaded = false,
                HomeScreenSectionsLoaded = false,
                GhostItems = 10,
                UseCondensedSummaries = true,
                ItemsMissingSummary = 5,
                RecommendationsEnabled = true,
                RecommendationPlaylistCount = 0,
                CategoriesWithoutPlaylist = 19,
            };

            var findings = HealthCheck.Evaluate(facts);

            Assert.NotEmpty(findings);
            Assert.All(findings, f =>
            {
                Assert.False(string.IsNullOrWhiteSpace(f.Id));
                Assert.False(string.IsNullOrWhiteSpace(f.Title));
                Assert.True(f.Detail.Length > 20, $"'{f.Id}' has no useful remedy");
            });
        }

        // ---- the failures this session found, which the check used to miss ----

        [Fact]
        public void TagConsolidationOnAndProducingNothingIsReported()
        {
            // Shipped broken and ran for weeks: the schema forbade the field the
            // prompt asked for, so every item came back with an empty tag list and
            // nothing anywhere said so.
            var findings = HealthCheck.Evaluate(new HealthFacts(
                DateTime.UtcNow,
                ConsolidateTags: true,
                StoredSummaries: 232,
                SummariesWithTags: 0));

            Assert.Contains(findings, f => f.Id == "summaries.notags");
        }

        [Fact]
        public void TagConsolidationProducingSomeTagsIsNotReported()
        {
            var findings = HealthCheck.Evaluate(new HealthFacts(
                DateTime.UtcNow,
                ConsolidateTags: true,
                StoredSummaries: 232,
                SummariesWithTags: 1));

            Assert.DoesNotContain(findings, f => f.Id == "summaries.notags");
        }

        [Fact]
        public void ASmallSampleWithNoTagsIsNotReported()
        {
            // A handful of items whose scraped tags were all production trivia
            // genuinely produce nothing. That is a correct answer, and the check has
            // to stay shy or it gets ignored.
            var findings = HealthCheck.Evaluate(new HealthFacts(
                DateTime.UtcNow,
                ConsolidateTags: true,
                StoredSummaries: 3,
                SummariesWithTags: 0));

            Assert.DoesNotContain(findings, f => f.Id == "summaries.notags");
        }

        [Fact]
        public void TagsOffIsNeverReportedHoweverManySummariesHaveNone()
        {
            var findings = HealthCheck.Evaluate(new HealthFacts(
                DateTime.UtcNow,
                ConsolidateTags: false,
                StoredSummaries: 232,
                SummariesWithTags: 0));

            Assert.DoesNotContain(findings, f => f.Id == "summaries.notags");
        }

        [Fact]
        public void ADistillationPassThatLostMostOfItsItemsIsReported()
        {
            // The measured shape: 27 stored, 185 written off after being paid for.
            var findings = HealthCheck.Evaluate(new HealthFacts(
                DateTime.UtcNow,
                LastSummaryPassDistilled: 27,
                LastSummaryPassFailed: 185));

            var finding = Assert.Single(findings, f => f.Id == "summaries.failing");
            Assert.Contains("185 of 212", finding.Title, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(212, 0)]     // a clean pass
        [InlineData(200, 12)]    // a few losses, not worth a panel
        [InlineData(106, 106)]   // exactly half is not "most"
        [InlineData(0, 0)]       // no pass since restart
        public void AHealthyOrUnremarkablePassIsNotReported(int distilled, int failed)
        {
            var findings = HealthCheck.Evaluate(new HealthFacts(
                DateTime.UtcNow,
                LastSummaryPassDistilled: distilled,
                LastSummaryPassFailed: failed));

            Assert.DoesNotContain(findings, f => f.Id == "summaries.failing");
        }
    }
}
