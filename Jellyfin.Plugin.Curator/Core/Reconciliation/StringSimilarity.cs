using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.Curator.Core.Reconciliation
{
    /// <summary>
    /// Name-similarity primitives for reconciliation. Clear over clever:
    /// normalization, classic Levenshtein, and token-set Jaccard.
    /// </summary>
    public static class StringSimilarity
    {
        /// <summary>
        /// Normalizes a category name for comparison: lowercase, punctuation
        /// replaced with spaces, whitespace collapsed. "Sci-Fi!" and "sci fi"
        /// normalize identically.
        /// </summary>
        /// <param name="name">The raw name.</param>
        /// <returns>The normalized form.</returns>
        public static string Normalize(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            var sb = new StringBuilder(name.Length);
            var pendingSpace = false;
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (pendingSpace && sb.Length > 0)
                    {
                        sb.Append(' ');
                    }

                    pendingSpace = false;
                    sb.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    pendingSpace = true;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Combined similarity of two already-normalized names in [0, 1]:
        /// the better of character-level (Levenshtein) and token-set (Jaccard)
        /// similarity. Character level catches singular/plural and small typos;
        /// token level catches word reorderings.
        /// </summary>
        /// <param name="a">First normalized name.</param>
        /// <param name="b">Second normalized name.</param>
        /// <returns>Similarity in [0, 1].</returns>
        public static double NormalizedNameSimilarity(string a, string b)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            if (a.Length == 0 || b.Length == 0)
            {
                return a.Length == b.Length ? 1.0 : 0.0;
            }

            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return 1.0;
            }

            var levenshtein = 1.0 - ((double)LevenshteinDistance(a, b) / Math.Max(a.Length, b.Length));
            return Math.Max(levenshtein, TokenJaccard(a, b));
        }

        /// <summary>
        /// Classic two-row Levenshtein edit distance.
        /// </summary>
        /// <param name="a">First string.</param>
        /// <param name="b">Second string.</param>
        /// <returns>The edit distance.</returns>
        public static int LevenshteinDistance(string a, string b)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            if (a.Length == 0)
            {
                return b.Length;
            }

            if (b.Length == 0)
            {
                return a.Length;
            }

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }

        private static double TokenJaccard(string a, string b)
        {
            var tokensA = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var tokensB = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            if (tokensA.Count == 0 || tokensB.Count == 0)
            {
                return 0.0;
            }

            var intersection = 0;
            foreach (var token in tokensA)
            {
                if (tokensB.Contains(token))
                {
                    intersection++;
                }
            }

            var union = tokensA.Count + tokensB.Count - intersection;
            return (double)intersection / union;
        }
    }
}
