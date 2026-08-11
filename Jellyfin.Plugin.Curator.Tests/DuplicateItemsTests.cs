using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Curator.Core;
using Jellyfin.Plugin.Curator.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Collapsing two library rows for one film.
    ///
    /// The real case: "Donnie Darko" and "Dexter" each appear twice on the owner's
    /// server — a director's cut beside a theatrical one, and a duplicated series —
    /// reaching the model with identical titles, years, genres and overviews, so it
    /// puts both in the same category and the row shows the poster twice.
    ///
    /// The failure mode is worse than the problem, which is what the strictness is
    /// for: the same library has "Freaky Friday" as 2003 and 1995, and a title-only
    /// rule would quietly remove a film.
    /// </summary>
    public class DuplicateItemsTests
    {
        private static MediaItemRecord Movie(string name, int? year, int? runtime = null, Guid? id = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Kind = MediaKind.Movie,
            Name = name,
            Year = year,
            RuntimeMinutes = runtime,
        };

        private static MediaItemRecord Series(string name, int? year, string? externalId = null) => new()
        {
            Id = Guid.NewGuid(),
            Kind = MediaKind.Series,
            Name = name,
            Year = year,
            ExternalId = externalId,
        };

        private static MediaItemRecord Scraped(string name, int? year, string externalId, int? runtime = null) => new()
        {
            Id = Guid.NewGuid(),
            Kind = MediaKind.Movie,
            Name = name,
            Year = year,
            RuntimeMinutes = runtime,
            ExternalId = externalId,
        };

        [Fact]
        public void TwoCutsOfOneFilmBecomeOne()
        {
            var theatrical = Movie("Donnie Darko", 2001, runtime: 113);
            var director = Movie("Donnie Darko", 2001, runtime: 133);

            var result = DuplicateItems.Collapse([theatrical, director]);

            var kept = Assert.Single(result.Items);
            Assert.Equal(director.Id, kept.Id);
            Assert.Equal(director.Id, result.Aliases[theatrical.Id]);
        }

        [Fact]
        public void TheLongerCutIsTheOneKept()
        {
            // A director's cut over a theatrical one, whichever order they scan in.
            var director = Movie("Donnie Darko", 2001, runtime: 133);
            var theatrical = Movie("Donnie Darko", 2001, runtime: 113);

            Assert.Equal(director.Id, Assert.Single(DuplicateItems.Collapse([director, theatrical]).Items).Id);
            Assert.Equal(director.Id, Assert.Single(DuplicateItems.Collapse([theatrical, director]).Items).Id);
        }

        [Fact]
        public void SameTitleDifferentYearIsTwoDifferentFilms()
        {
            // The case that makes a title-only rule dangerous. Both survive.
            var newer = Movie("Freaky Friday", 2003);
            var older = Movie("Freaky Friday", 1995);

            var result = DuplicateItems.Collapse([newer, older]);

            Assert.Equal(2, result.Items.Count);
            Assert.Empty(result.Aliases);
        }

        [Fact]
        public void AMissingYearNeverMatchesAKnownOne()
        {
            var known = Movie("Solaris", 1972);
            var unknown = Movie("Solaris", null);

            Assert.Equal(2, DuplicateItems.Collapse([known, unknown]).Items.Count);
        }

        [Fact]
        public void AFilmAndASeriesOfTheSameNameAreNotDuplicates()
        {
            var film = Movie("Fargo", 1996);
            var show = Series("Fargo", 1996);

            Assert.Equal(2, DuplicateItems.Collapse([film, show]).Items.Count);
        }

        [Fact]
        public void MatchingIgnoresCaseAndSurroundingSpace()
        {
            var a = Movie("Donnie Darko", 2001, runtime: 113);
            var b = Movie("  donnie darko ", 2001, runtime: 133);

            Assert.Single(DuplicateItems.Collapse([a, b]).Items);
        }

        [Fact]
        public void EqualRuntimesFallBackToLibraryOrderSoTheChoiceIsStable()
        {
            // Two rows with nothing to choose between them must not alternate from
            // run to run — that would churn the row for no reason.
            var first = Movie("Dexter", 2006);
            var second = Movie("Dexter", 2006);

            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(first.Id, Assert.Single(DuplicateItems.Collapse([first, second]).Items).Id);
            }
        }

        [Fact]
        public void TheSurvivingListKeepsItsOriginalOrder()
        {
            // The prompt numbers items in list order, so a collapse that reshuffled
            // would change every index for no reason.
            var a = Movie("Alpha", 2001);
            var dupe1 = Movie("Beta", 2002, runtime: 90);
            var dupe2 = Movie("Beta", 2002, runtime: 120);
            var c = Movie("Gamma", 2003);

            var items = DuplicateItems.Collapse([a, dupe1, dupe2, c]).Items;

            Assert.Equal(["Alpha", "Beta", "Gamma"], items.Select(i => i.Name).ToList());
        }

        [Fact]
        public void ALibraryWithNoDuplicatesIsReturnedUntouched()
        {
            var items = new[] { Movie("A", 2001), Movie("B", 2002), Series("C", 2003) };

            var result = DuplicateItems.Collapse(items);

            Assert.Same(items, result.Items);
            Assert.Empty(result.Aliases);
        }

        // ---- the two exact identities, which title and year cannot reach ----

        [Fact]
        public void TwoCutsUnderDifferentTitlesAndYearsCollapseOnTheProviderId()
        {
            // The case the strict key was always going to miss, and the reason this
            // exists: one film, two titles, two years, one TMDb ID.
            var theatrical = Scraped("Blade Runner", 1982, "tmdb:78", runtime: 117);
            var finalCut = Scraped("Blade Runner: The Final Cut", 2007, "tmdb:78", runtime: 117);

            var result = DuplicateItems.Collapse([theatrical, finalCut]);

            Assert.Single(result.Items);
            Assert.Equal(theatrical.Id, result.Aliases[finalCut.Id]);
        }

        [Fact]
        public void FreakyFridaySurvivesTheProviderIdRuleToo()
        {
            // The rule that must not regress. Two films, one title, two TMDb IDs —
            // and now the IDs are what keeps them apart rather than the years.
            var newer = Scraped("Freaky Friday", 2003, "tmdb:10330");
            var older = Scraped("Freaky Friday", 1995, "tmdb:37725");

            Assert.Equal(2, DuplicateItems.Collapse([newer, older]).Items.Count);
        }

        [Fact]
        public void ProviderIdsFromDifferentProvidersNeverMatchEachOther()
        {
            // "tmdb:78" and "imdb:78" are not the same claim about the world.
            var a = Scraped("Some Film", 2001, "tmdb:78");
            var b = Scraped("Other Film", 1999, "imdb:78");

            Assert.Equal(2, DuplicateItems.Collapse([a, b]).Items.Count);
        }

        [Fact]
        public void AFilmAndASeriesSharingAProviderIdAreStillTwoThings()
        {
            var film = Scraped("Fargo", 1996, "tmdb:275");
            var show = Series("Fargo", 2014, "tmdb:275");

            Assert.Equal(2, DuplicateItems.Collapse([film, show]).Items.Count);
        }

        [Fact]
        public void ProviderIdMatchingCanBeSwitchedOff()
        {
            var theatrical = Scraped("Blade Runner", 1982, "tmdb:78");
            var finalCut = Scraped("Blade Runner: The Final Cut", 2007, "tmdb:78");

            var result = DuplicateItems.Collapse([theatrical, finalCut], matchOnExternalIds: false);

            Assert.Equal(2, result.Items.Count);
        }

        [Fact]
        public void AnItemMergedInJellyfinFoldsOntoItsPrimaryHoweverItsFieldsRead()
        {
            // The owner has already told Jellyfin these are one film. Title, year and
            // provider ID all disagree and none of that matters.
            var primary = Movie("Nosferatu", 1922, runtime: 94);
            var alternate = new MediaItemRecord
            {
                Id = Guid.NewGuid(),
                Kind = MediaKind.Movie,
                Name = "Nosferatu (restored)",
                Year = 1994,
                RuntimeMinutes = 96,
                PrimaryVersionId = primary.Id,
            };

            var result = DuplicateItems.Collapse([primary, alternate]);

            var kept = Assert.Single(result.Items);
            Assert.Equal(primary.Id, kept.Id);
            Assert.Equal(primary.Id, result.Aliases[alternate.Id]);
        }

        [Fact]
        public void ThePrimaryWinsEvenWhenTheAlternateRunsLonger()
        {
            // The one place the longest-runtime rule gives way. Every other client
            // draws the primary and hides the alternate behind a version picker on
            // it, so keeping the alternate would put a card on the home screen that
            // appears nowhere else in the server.
            var primary = Movie("Brazil", 1985, runtime: 132);
            var alternate = new MediaItemRecord
            {
                Id = Guid.NewGuid(),
                Kind = MediaKind.Movie,
                Name = "Brazil",
                Year = 1985,
                RuntimeMinutes = 143,
                PrimaryVersionId = primary.Id,
            };

            Assert.Equal(primary.Id, Assert.Single(DuplicateItems.Collapse([alternate, primary]).Items).Id);
        }

        [Fact]
        public void AnAlternateWhosePrimaryIsNotInTheScanKeepsItsOwnIdentity()
        {
            // The primary was filtered out — orphaned by a removed library folder,
            // say. Keying the alternate on a row that is not here would lose it.
            var alternate = new MediaItemRecord
            {
                Id = Guid.NewGuid(),
                Kind = MediaKind.Movie,
                Name = "Stalker",
                Year = 1979,
                PrimaryVersionId = Guid.NewGuid(),
            };
            var other = Movie("Solaris", 1972);

            Assert.Equal(2, DuplicateItems.Collapse([alternate, other]).Items.Count);
        }

        [Fact]
        public void AVersionLinkPointingAtItselfDoesNotHangTheScan()
        {
            // These links come out of a database Curator does not own.
            var id = Guid.NewGuid();
            var self = new MediaItemRecord
            {
                Id = id,
                Kind = MediaKind.Movie,
                Name = "Loop",
                Year = 2000,
                PrimaryVersionId = id,
            };

            Assert.Single(DuplicateItems.Collapse([self]).Items);
        }

        [Fact]
        public void TwoItemsPointingAtEachOtherStillCollapseToOne()
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var a = new MediaItemRecord
            {
                Id = first,
                Kind = MediaKind.Movie,
                Name = "A",
                Year = 2000,
                PrimaryVersionId = second,
            };
            var b = new MediaItemRecord
            {
                Id = second,
                Kind = MediaKind.Movie,
                Name = "B",
                Year = 2001,
                PrimaryVersionId = first,
            };

            var result = DuplicateItems.Collapse([a, b]);

            Assert.Single(result.Items);
            Assert.Single(result.Aliases);
        }

        [Fact]
        public void SurvivingIdsAgreesWithTheCollapseItBacksUp()
        {
            // The row-side backstop must not be a second opinion about what a
            // duplicate is — it is the same function over the same keys.
            var theatrical = Scraped("Blade Runner", 1982, "tmdb:78");
            var finalCut = Scraped("Blade Runner: The Final Cut", 2007, "tmdb:78");
            var other = Movie("Solaris", 1972);
            var items = new[] { theatrical, finalCut, other };

            var surviving = DuplicateItems.SurvivingIds(items);

            Assert.Equal(
                DuplicateItems.Collapse(items).Items.Select(i => i.Id).ToHashSet(),
                surviving);
            Assert.DoesNotContain(finalCut.Id, surviving);
        }

        // ---- history must survive the collapse ----

        [Fact]
        public void ActivityOnTheDroppedRowMovesToTheOneKept()
        {
            // The whole reason aliases exist. They watched the theatrical cut; the
            // director's cut is what gets sent; the film must not read as unseen.
            var theatrical = Guid.NewGuid();
            var director = Guid.NewGuid();
            var activity = new Dictionary<Guid, UserActivity>
            {
                [theatrical] = new() { Played = true, PlayCount = 2 },
            };

            var folded = DuplicateItems.FoldActivity(
                activity,
                new Dictionary<Guid, Guid> { [theatrical] = director });

            Assert.True(folded[director].Played);
            Assert.Equal(2, folded[director].PlayCount);
            Assert.False(folded.ContainsKey(theatrical));
        }

        [Fact]
        public void ActivityOnBothRowsMergesToTheStrongerSignal()
        {
            // Played one, opened the other: they have watched the film once, not
            // been ambivalent about it.
            var dropped = Guid.NewGuid();
            var kept = Guid.NewGuid();
            var activity = new Dictionary<Guid, UserActivity>
            {
                [dropped] = new() { Played = true, PlayCount = 3, UserRating = 9 },
                [kept] = new() { Played = false, PlayCount = 0, IsFavorite = true },
            };

            var folded = DuplicateItems.FoldActivity(
                activity, new Dictionary<Guid, Guid> { [dropped] = kept });

            var merged = Assert.Single(folded).Value;
            Assert.True(merged.Played);
            Assert.Equal(3, merged.PlayCount);
            Assert.True(merged.IsFavorite);
            Assert.Equal(9, merged.UserRating);
        }

        [Fact]
        public void FoldingWithNoAliasesChangesNothing()
        {
            var id = Guid.NewGuid();
            var activity = new Dictionary<Guid, UserActivity> { [id] = new() { Played = true } };

            Assert.Same(activity, DuplicateItems.FoldActivity(activity, new Dictionary<Guid, Guid>()));
        }
    }
}
