using System.Text.Json.Nodes;
using Jellyfin.Plugin.Curator.Core.Footer;
using Xunit;

namespace Jellyfin.Plugin.Curator.Tests
{
    /// <summary>
    /// The footer: what may be linked, what reaches the page, and what is written
    /// into the other plugin's configuration.
    ///
    /// <para>
    /// This is the only part of Curator that puts owner-typed text into somebody
    /// else's browser, so most of these are about escaping rather than about
    /// appearance. The rest of the plugin escapes LLM output on a config page seen by
    /// one admin; this escapes on every page seen by every viewer.
    /// </para>
    /// </summary>
    public class FooterTests
    {
        private static FooterModel Model(
            string heading = "Heading",
            string text = "Text",
            FooterLinkModel[]? links = null,
            bool homeOnly = true)
            => new(heading, text, links ?? [], homeOnly);

        // ---- what may be linked ----

        [Theory]
        [InlineData("https://example.com", true)]
        [InlineData("http://example.com/path?a=b", true)]
        [InlineData("mailto:someone@example.com", true)]
        [InlineData("/web/index.html#/home", true)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        public void OrdinaryAddressesAreAllowedAndBlanksAreNot(string url, bool expected)
        {
            Assert.Equal(expected, FooterMarkup.IsSafeUrl(url));
        }

        [Theory]
        [InlineData("javascript:alert(1)")]
        [InlineData("JavaScript:alert(1)")]
        [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
        [InlineData("vbscript:msgbox")]
        [InlineData("file:///etc/passwd")]
        public void SchemesThatCanRunCodeOrReadTheDiskAreRefused(string url)
        {
            // An allow-list, not a block-list — these are the cases it is FOR, but a
            // scheme nobody has thought of is refused by default rather than by
            // having been listed here.
            Assert.False(FooterMarkup.IsSafeUrl(url));
        }

        [Fact]
        public void AProtocolRelativeAddressIsNotTreatedAsAPath()
        {
            // "//evil.example" looks like a path and is not one: the browser reads it
            // as the current scheme plus another host. A naive "starts with /" test
            // lets it through, which is exactly the mistake worth pinning.
            Assert.False(FooterMarkup.IsSafeUrl("//evil.example/x"));
            Assert.True(FooterMarkup.IsSafeUrl("/evil.example/x"));
        }

        [Fact]
        public void UnusableLinksAreDroppedRatherThanDrawnBroken()
        {
            var links = FooterMarkup.UsableLinks(
            [
                new FooterLinkModel("Good", "https://example.com"),
                new FooterLinkModel("", "https://example.com"),
                new FooterLinkModel("No address", ""),
                new FooterLinkModel("Dangerous", "javascript:alert(1)"),
            ]);

            var kept = Assert.Single(links);
            Assert.Equal("Good", kept.Label);
        }

        // ---- what reaches the page ----

        [Fact]
        public void AFooterWithNothingInItIsNotPublished()
        {
            // Otherwise an enabled-but-empty footer draws a rule across the bottom of
            // every page and puzzles whoever finds it.
            Assert.False(FooterMarkup.HasContent(Model(heading: "", text: "", links: [])));
            Assert.True(FooterMarkup.HasContent(Model(heading: "", text: "Something")));
            Assert.True(FooterMarkup.HasContent(
                Model(heading: "", text: "", links: [new FooterLinkModel("A", "https://example.com")])));
        }

        [Fact]
        public void AHeadingCannotCloseTheScriptBlockItIsInside()
        {
            // The one character that escapes a JSON string embedded in a <script> is
            // "<" beginning a closing tag. Everything else JSON already handles.
            var built = FooterMarkup.Build(Model(heading: "</script><img src=x onerror=alert(1)>"));

            Assert.DoesNotContain("</script><img", built, System.StringComparison.OrdinalIgnoreCase);

            // Case-insensitive: System.Text.Json's default encoder already escapes
            // "<" as <, and the explicit replace in FooterMarkup emits <.
            // Which one wins is not the point — that the raw character never reaches
            // the page is.
            Assert.Contains("\\u003c", built, System.StringComparison.OrdinalIgnoreCase);

            // Exactly one closing tag: the real one at the end.
            var closings = built.Split("</script>").Length - 1;
            Assert.Equal(1, closings);
        }

        [Fact]
        public void TextIsCarriedAsDataAndAssignedAsTextNotMarkup()
        {
            var built = FooterMarkup.Build(Model(text: "Bold & <b>brave</b>"));

            // The value survives as data...
            Assert.Contains("JSON.parse", built, System.StringComparison.Ordinal);

            // ...and the script only ever writes it through textContent. innerHTML
            // anywhere in this fragment would make every escaping test above moot.
            Assert.DoesNotContain("innerHTML", built, System.StringComparison.Ordinal);
            Assert.Contains("textContent", built, System.StringComparison.Ordinal);
        }

        [Fact]
        public void TheHomeOnlyChoiceTravelsToTheClient()
        {
            Assert.Contains("\"homeOnly\":true", Compact(FooterMarkup.Build(Model(homeOnly: true))), System.StringComparison.Ordinal);
            Assert.Contains("\"homeOnly\":false", Compact(FooterMarkup.Build(Model(homeOnly: false))), System.StringComparison.Ordinal);
        }

        private static string Compact(string s) => s.Replace(" ", string.Empty, System.StringComparison.Ordinal);

        // ---- what is written into the other plugin's configuration ----

        [Fact]
        public void MergingAddsOneEntryAndIsIdempotent()
        {
            var config = JsonNode.Parse("""{"Transformations":[]}""");

            Assert.True(FooterTransformationMerger.Merge(config, "<script>x</script>"));
            Assert.Single(config!["Transformations"]!.AsArray());

            // Saving again with the same footer must not write a second copy, or the
            // fragment stacks once per save.
            Assert.False(FooterTransformationMerger.Merge(config, "<script>x</script>"));
            Assert.Single(config["Transformations"]!.AsArray());
        }

        [Fact]
        public void TheReplacementPutsTheAnchorBackSoTheDocumentStaysWellFormed()
        {
            var config = JsonNode.Parse("""{"Transformations":[]}""");
            FooterTransformationMerger.Merge(config, "<script>x</script>");

            var entry = config!["Transformations"]!.AsArray()[0]!.AsObject();

            Assert.Equal("</body>", (string?)entry["SearchText"]);
            Assert.Equal("<script>x</script></body>", (string?)entry["ReplaceText"]);
            Assert.Equal("index.html", (string?)entry["FilenamePattern"]);
        }

        [Fact]
        public void SwitchingTheFooterOffRemovesTheEntryRatherThanEmptyingIt()
        {
            // Curator is writing into another plugin's settings. A disabled fragment
            // left behind is litter in somebody else's house, and worse, it would go
            // on being applied to every page.
            var config = JsonNode.Parse("""{"Transformations":[]}""");
            FooterTransformationMerger.Merge(config, "<script>x</script>");

            Assert.True(FooterTransformationMerger.Merge(config, null));
            Assert.Empty(config!["Transformations"]!.AsArray());

            // And removing what is already absent is not a change.
            Assert.False(FooterTransformationMerger.Merge(config, null));
        }

        [Fact]
        public void AnotherPluginsTransformationsAreNeverTouched()
        {
            var config = JsonNode.Parse(
                """{"Transformations":[{"Id":"11111111-1111-1111-1111-111111111111","FilenamePattern":"index.html"}]}""");

            FooterTransformationMerger.Merge(config, "<script>x</script>");
            Assert.Equal(2, config!["Transformations"]!.AsArray().Count);

            FooterTransformationMerger.Merge(config, null);
            var left = Assert.Single(config["Transformations"]!.AsArray());
            Assert.Equal("11111111-1111-1111-1111-111111111111", (string?)left!["Id"]);
        }

        [Fact]
        public void AnIdThatCameBackWithoutItsDashesIsStillRecognised()
        {
            // The measured bug. The property is a Guid on the other plugin's type, so
            // what comes back is whatever ITS serializer chose — on the owner's
            // server the round trip turned c47a1e05-6b3f-... into c47a1e056b3f...,
            // dashes gone. String equality then failed to recognise Curator's own
            // entry: switching the footer off removed nothing, and the log cheerfully
            // reported "the footer is off and nothing was published" while the
            // fragment stayed in the file.
            var config = JsonNode.Parse(
                """{"Transformations":[{"Id":"c47a1e056b3f4d219f760a2c8e5b1d44","ReplaceText":"old</body>"}]}""");

            Assert.True(FooterTransformationMerger.Merge(config, null));
            Assert.Empty(config!["Transformations"]!.AsArray());
        }

        [Fact]
        public void RepublishingReplacesTheEntryRatherThanStackingASecond()
        {
            // The other half of the same bug, and the one that would have been worse
            // over time: an entry it could not recognise is an entry it appends
            // beside, so every save added another fragment to every page.
            var config = JsonNode.Parse(
                """{"Transformations":[{"Id":"C47A1E056B3F4D219F760A2C8E5B1D44","ReplaceText":"old</body>"}]}""");

            Assert.True(FooterTransformationMerger.Merge(config, "<script>new</script>"));

            var entry = Assert.Single(config!["Transformations"]!.AsArray());
            Assert.Equal("<script>new</script></body>", (string?)entry!["ReplaceText"]);
        }

        [Fact]
        public void TheFragmentGoesIntoThePageContainerRatherThanTheDocumentBody()
        {
            // Jellyfin lays out .skinBody and .mainAnimatedPage as position:absolute,
            // so body's flow is empty and anything appended there renders at the TOP
            // of the document under the fixed header — which is exactly where the
            // first version of this put the footer.
            var built = FooterMarkup.Build(Model());

            Assert.Contains("mainAnimatedPage", built, System.StringComparison.Ordinal);
            Assert.DoesNotContain("document.body.appendChild", built, System.StringComparison.Ordinal);

            // .skinBody is pointer-events:none, so a descendant that does not
            // re-enable them renders links nobody can click.
            Assert.Contains("pointer-events:auto", built, System.StringComparison.Ordinal);
        }

        [Fact]
        public void TheCamelCaseFormTheServerSendsIsRecognised()
        {
            // The server serializes plugin configuration as camelCase over HTTP while
            // the C# type is PascalCase. A naive implementation creates a SECOND
            // array the plugin ignores, and the footer silently never appears.
            var config = JsonNode.Parse(
                """{"transformations":[{"id":"c47a1e05-6b3f-4d21-9f76-0a2c8e5b1d44","replaceText":"old</body>"}]}""");

            Assert.True(FooterTransformationMerger.Merge(config, "<script>new</script>"));

            var array = config!["transformations"]!.AsArray();
            Assert.Single(array);
            Assert.Null(config["Transformations"]);
        }
    }
}
