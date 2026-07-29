using Jellyfin.Plugin.Curator.Core.Reconciliation;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    public class StringSimilarityTests
    {
        [Theory]
        [InlineData("Sci-Fi!", "sci fi")]
        [InlineData("  Comfort   Rewatch  ", "comfort rewatch")]
        [InlineData("Dumb & Perfect", "dumb perfect")]
        [InlineData("QUIETLY devastating", "quietly devastating")]
        [InlineData("", "")]
        [InlineData("?!&", "")]
        public void Normalize_CanonicalizesCaseSpacingAndPunctuation(string input, string expected)
        {
            Assert.Equal(expected, StringSimilarity.Normalize(input));
        }

        [Theory]
        [InlineData("abc", "abc", 0)]
        [InlineData("", "abc", 3)]
        [InlineData("abc", "", 3)]
        [InlineData("kitten", "sitting", 3)]
        [InlineData("comfort rewatch", "comfort rewatches", 2)]
        public void LevenshteinDistance_ClassicCases(string a, string b, int expected)
        {
            Assert.Equal(expected, StringSimilarity.LevenshteinDistance(a, b));
        }

        [Fact]
        public void Similarity_IdenticalNames_IsOne()
        {
            Assert.Equal(1.0, StringSimilarity.NormalizedNameSimilarity("comfort rewatch", "comfort rewatch"));
        }

        [Fact]
        public void Similarity_PluralVariant_IsHigh()
        {
            var similarity = StringSimilarity.NormalizedNameSimilarity("comfort rewatch", "comfort rewatches");

            Assert.True(similarity >= 0.85, $"expected >= 0.85, got {similarity}");
        }

        [Fact]
        public void Similarity_ReorderedTokens_IsCaughtByJaccard()
        {
            var similarity = StringSimilarity.NormalizedNameSimilarity("neo noir classics", "classics neo noir");

            Assert.Equal(1.0, similarity);
        }

        [Fact]
        public void Similarity_SharedPrefixDifferentSubject_StaysBelowThreshold()
        {
            var similarity = StringSimilarity.NormalizedNameSimilarity("slow burn horror", "slow burn romance");

            Assert.True(similarity < 0.85, $"expected < 0.85, got {similarity}");
        }

        [Fact]
        public void Similarity_EmptyEdges()
        {
            Assert.Equal(1.0, StringSimilarity.NormalizedNameSimilarity(string.Empty, string.Empty));
            Assert.Equal(0.0, StringSimilarity.NormalizedNameSimilarity(string.Empty, "vibes"));
        }
    }
}
