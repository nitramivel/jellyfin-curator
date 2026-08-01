using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Curator.Core.Playlists;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// Who a category's playlists belong to.
    ///
    /// One line of logic that reads as obviously correct at every call site and was
    /// wrong at one of them: the nightly reconcile pass walked every stored
    /// definition and handed the full target list to all of them, so each cleanup
    /// gave one viewer's private rows to the whole household. Measured on a live
    /// server before the fix — 102 definitions, 80 of them personal, every single
    /// one holding a playlist for all six users.
    /// </summary>
    public class CategoryAudienceTests
    {
        private static readonly Guid Alice = Guid.NewGuid();
        private static readonly Guid Bob = Guid.NewGuid();
        private static readonly Guid Carol = Guid.NewGuid();
        private static readonly IReadOnlyList<Guid> Everyone = [Alice, Bob, Carol];

        [Fact]
        public void ASharedCategoryGoesToEveryoneTargeted()
        {
            Assert.Equal(Everyone, CategoryAudience.For(null, Everyone));
        }

        [Fact]
        public void APersonalCategoryGoesToItsOwnerAndNobodyElse()
        {
            Assert.Equal([Bob], CategoryAudience.For(Bob, Everyone));
        }

        [Fact]
        public void APersonalCategoryWhoseOwnerIsNoLongerTargetedGoesToNobody()
        {
            // The dangerous case. The natural fallback for "this category has an
            // owner I cannot place" is the full target list, and the full list is
            // exactly the wrong answer — that is the bug, in one line.
            Assert.Empty(CategoryAudience.For(Bob, [Alice, Carol]));
        }

        [Fact]
        public void AnEmptyOwnerGuidCountsAsShared()
        {
            // A definition written before owners existed, or one whose owner was
            // cleared, is a shared category rather than a personal one belonging to
            // nobody — otherwise it would silently stop being built for anyone.
            Assert.Equal(Everyone, CategoryAudience.For(Guid.Empty, Everyone));
        }

        [Fact]
        public void NoTargetUsersMeansNoAudienceWhicheverKindItIs()
        {
            Assert.Empty(CategoryAudience.For(null, []));
            Assert.Empty(CategoryAudience.For(Bob, []));
        }

        [Fact]
        public void TheAudienceIsNeverWiderThanTheTargetList()
        {
            // The property that actually matters, over every shape of input: a
            // category can lose viewers from its audience but must never gain one
            // the run was not building for.
            foreach (Guid? owner in new Guid?[] { null, Alice, Bob, Carol, Guid.Empty, Guid.NewGuid() })
            {
                foreach (var targets in new IReadOnlyList<Guid>[] { [], [Alice], [Alice, Bob], Everyone })
                {
                    var audience = CategoryAudience.For(owner, targets);
                    Assert.All(audience, id => Assert.Contains(id, targets));
                    Assert.True(audience.Count <= targets.Count);
                }
            }
        }

        [Fact]
        public void APersonalCategoryNeverResolvesToMoreThanOneViewer()
        {
            foreach (var targets in new IReadOnlyList<Guid>[] { [Alice], [Alice, Bob], Everyone })
            {
                Assert.True(CategoryAudience.For(Alice, targets).Count <= 1);
            }
        }
    }
}
