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

        private static MediaItemRecord Series(string name, int? year) => new()
        {
            Id = Guid.NewGuid(),
            Kind = MediaKind.Series,
            Name = name,
            Year = year,
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
