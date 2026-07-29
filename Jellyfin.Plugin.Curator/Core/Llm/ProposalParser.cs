using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Curator.Core.Models;

namespace Jellyfin.Plugin.Curator.Core.Llm
{
    /// <summary>
    /// The outcome of parsing one batch response: the validated proposals plus
    /// counts of what was discarded, for logging.
    /// </summary>
    /// <param name="Proposals">The validated proposals.</param>
    /// <param name="DiscardedMemberCount">Members dropped for referencing indexes outside the batch, or duplicated.</param>
    /// <param name="DiscardedCategoryCount">Categories dropped for having no name or no valid members.</param>
    public sealed record ParseResult(
        IReadOnlyList<CategoryProposal> Proposals,
        int DiscardedMemberCount,
        int DiscardedCategoryCount);

    /// <summary>
    /// Strict parser for LLM batch responses. The model must return only
    /// batch-local integer indexes; any index outside the input set is
    /// discarded. This is the enforcement point for the "never let the model
    /// invent items" invariant.
    /// </summary>
    public static class ProposalParser
    {
        /// <summary>
        /// Parses a raw model response against the batch that produced it.
        /// </summary>
        /// <param name="responseText">The model's text output.</param>
        /// <param name="batch">The batch the response describes; indexes map into it.</param>
        /// <returns>Validated proposals and discard counts.</returns>
        /// <exception cref="FormatException">The response contains no parseable JSON object of the required shape.</exception>
        public static ParseResult Parse(string responseText, IReadOnlyList<MediaItemRecord> batch)
        {
            ArgumentNullException.ThrowIfNull(responseText);
            ArgumentNullException.ThrowIfNull(batch);

            var json = ExtractJsonObject(responseText);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException("Model response is not valid JSON.", ex);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("categories", out var categories)
                    || categories.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("Model response lacks a top-level \"categories\" array.");
                }

                var proposals = new List<CategoryProposal>();
                var discardedMembers = 0;
                var discardedCategories = 0;

                foreach (var category in categories.EnumerateArray())
                {
                    if (category.ValueKind != JsonValueKind.Object)
                    {
                        discardedCategories++;
                        continue;
                    }

                    var name = category.TryGetProperty("name", out var nameElement)
                        && nameElement.ValueKind == JsonValueKind.String
                            ? nameElement.GetString()?.Trim()
                            : null;

                    if (string.IsNullOrEmpty(name))
                    {
                        discardedCategories++;
                        continue;
                    }

                    var description = category.TryGetProperty("description", out var descriptionElement)
                        && descriptionElement.ValueKind == JsonValueKind.String
                            ? descriptionElement.GetString()!.Trim()
                            : string.Empty;

                    var members = new List<Guid>();
                    var seen = new HashSet<int>();
                    if (category.TryGetProperty("members", out var membersElement)
                        && membersElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var member in membersElement.EnumerateArray())
                        {
                            // Hard requirement: only indexes from the input set survive.
                            if (member.ValueKind == JsonValueKind.Number
                                && member.TryGetInt32(out var index)
                                && index >= 0
                                && index < batch.Count
                                && seen.Add(index))
                            {
                                members.Add(batch[index].Id);
                            }
                            else
                            {
                                discardedMembers++;
                            }
                        }
                    }

                    if (members.Count == 0)
                    {
                        discardedCategories++;
                        continue;
                    }

                    proposals.Add(new CategoryProposal
                    {
                        Name = name,
                        Description = description,
                        Members = members,
                    });
                }

                return new ParseResult(proposals, discardedMembers, discardedCategories);
            }
        }

        /// <summary>
        /// Extracts the first top-level JSON object from model output, tolerating
        /// code fences and stray prose around it.
        /// </summary>
        private static string ExtractJsonObject(string text)
        {
            var start = text.IndexOf('{', StringComparison.Ordinal);
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                throw new FormatException("Model response contains no JSON object.");
            }

            return text[start..(end + 1)];
        }
    }
}
