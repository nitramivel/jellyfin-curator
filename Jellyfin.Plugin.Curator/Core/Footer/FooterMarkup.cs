using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Curator.Core.Footer
{
    /// <summary>One link, reduced to what the markup needs.</summary>
    /// <param name="Label">The text shown.</param>
    /// <param name="Url">Where it points.</param>
    public sealed record FooterLinkModel(string Label, string Url);

    /// <summary>
    /// Everything the footer draws.
    /// </summary>
    /// <param name="Heading">Optional heading line.</param>
    /// <param name="Text">Optional body line.</param>
    /// <param name="Links">Links, in order.</param>
    /// <param name="HomeOnly">Whether it is drawn only on the home screen.</param>
    public sealed record FooterModel(
        string Heading,
        string Text,
        IReadOnlyList<FooterLinkModel> Links,
        bool HomeOnly);

    /// <summary>
    /// Builds the fragment injected into the Jellyfin web client.
    ///
    /// <para>
    /// Pure, so the awkward half of this feature — escaping, and what counts as a
    /// safe URL — is testable without a server or a browser. The output is a
    /// self-contained <c>&lt;script&gt;</c>: it builds the footer through the DOM
    /// rather than writing HTML, so a heading containing <c>&lt;/script&gt;</c>
    /// cannot end the block it is inside, and a link labelled with a quote cannot
    /// break out of an attribute. Every value crosses into the page as a JSON
    /// string and is assigned to <c>textContent</c>, never to <c>innerHTML</c>.
    /// </para>
    ///
    /// <para>
    /// Jellyfin's web client is a single-page app, so the footer is drawn once and
    /// then shown or hidden as the route changes; rebuilding it per navigation would
    /// flicker. Its own styles are scoped under one class and use Jellyfin's CSS
    /// custom properties where they exist, so it inherits the server's theme instead
    /// of fighting it.
    /// </para>
    /// </summary>
    public static class FooterMarkup
    {
        /// <summary>
        /// The marker Curator's fragment is wrapped in, so it can be found and
        /// replaced without disturbing anything else in the file.
        /// </summary>
        public const string Marker = "curator-footer";

        /// <summary>
        /// Whether a URL is safe to put in an anchor.
        /// </summary>
        /// <remarks>
        /// An allow-list, not a block-list. <c>javascript:</c> in an href executes on
        /// click with the page's full privileges, and a footer is the last place
        /// anybody would look for that — so anything that is not plainly http, https,
        /// mailto or a same-site path is dropped rather than sanitised. Being strict
        /// costs an admin a rare link; being permissive costs everyone.
        /// </remarks>
        /// <param name="url">The candidate.</param>
        /// <returns>Whether it may be linked.</returns>
        public static bool IsSafeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var trimmed = url.Trim();

            // A relative path stays on this server and cannot carry a scheme.
            // "//evil.example" is protocol-relative and is NOT a path, hence the
            // second test.
            if (trimmed.StartsWith('/') && !trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }

            return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The links worth rendering: labelled, safely addressed, in order.
        /// </summary>
        /// <param name="links">The configured links.</param>
        /// <returns>The ones that will be drawn.</returns>
        public static IReadOnlyList<FooterLinkModel> UsableLinks(IEnumerable<FooterLinkModel>? links)
        {
            if (links is null)
            {
                return [];
            }

            return [.. links
                .Where(l => l is not null
                    && !string.IsNullOrWhiteSpace(l.Label)
                    && IsSafeUrl(l.Url))
                .Select(l => new FooterLinkModel(l.Label.Trim(), l.Url.Trim()))];
        }

        /// <summary>
        /// Whether this footer would draw anything at all.
        /// </summary>
        /// <remarks>
        /// An enabled footer with nothing in it is a horizontal rule at the bottom of
        /// the page and a puzzle for whoever finds it. Nothing is injected in that
        /// case.
        /// </remarks>
        /// <param name="model">The footer.</param>
        /// <returns>Whether it has content.</returns>
        public static bool HasContent(FooterModel model)
        {
            ArgumentNullException.ThrowIfNull(model);

            return !string.IsNullOrWhiteSpace(model.Heading)
                || !string.IsNullOrWhiteSpace(model.Text)
                || UsableLinks(model.Links).Count > 0;
        }

        /// <summary>
        /// Builds the injectable fragment.
        /// </summary>
        /// <param name="model">What to draw.</param>
        /// <returns>A script element, ready to be spliced into the document.</returns>
        public static string Build(FooterModel model)
        {
            ArgumentNullException.ThrowIfNull(model);

            var payload = JsonSerializer.Serialize(new
            {
                heading = model.Heading?.Trim() ?? string.Empty,
                text = model.Text?.Trim() ?? string.Empty,
                links = UsableLinks(model.Links).Select(l => new { label = l.Label, url = l.Url }),
                homeOnly = model.HomeOnly,
            });

            // The payload is embedded as JSON and read with JSON.parse rather than
            // interpolated into expressions, so the only character that could escape
            // the context is "<" starting a closing tag — which JavaScriptEncoder
            // does not escape by default. Handled explicitly below.
            var safePayload = payload
                .Replace("<", "\\u003c", StringComparison.Ordinal)
                .Replace(">", "\\u003e", StringComparison.Ordinal)
                .Replace("&", "\\u0026", StringComparison.Ordinal);

            var script = Template.Replace("{PAYLOAD}", safePayload, StringComparison.Ordinal);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"<script id=\"{Marker}\" defer>{script}</script>");
        }

        /// <summary>
        /// The client-side half.
        /// </summary>
        /// <remarks>
        /// Written as ES5-compatible script on purpose: it is injected into a page
        /// whose bundler and target browsers are not ours to assume, and a syntax
        /// error here is a blank footer on somebody else's server with nothing in any
        /// log to explain it.
        /// </remarks>
        private const string Template =
            """
            (function () {
              var data = JSON.parse('{PAYLOAD}');
              var ID = 'curatorFooterEl';

              function css() {
                if (document.getElementById('curatorFooterCss')) { return; }
                var s = document.createElement('style');
                s.id = 'curatorFooterCss';
                s.textContent = [
                  '.curatorFooter{',
                  '  margin:3.5em 0 0;padding:2.2em 1.5em 2.6em;',
                  '  border-top:1px solid rgba(127,127,127,0.22);',
                  '  background:linear-gradient(180deg,rgba(127,127,127,0.05),rgba(127,127,127,0.11));',
                  '  text-align:center;font-size:0.95em;',
                  '}',
                  '.curatorFooterInner{max-width:60em;margin:0 auto;}',
                  '.curatorFooterHeading{',
                  '  margin:0 0 0.45em;font-size:1.18em;font-weight:600;letter-spacing:0.01em;',
                  '}',
                  '.curatorFooterText{margin:0;opacity:0.72;line-height:1.55;}',
                  '.curatorFooterLinks{',
                  '  display:flex;flex-wrap:wrap;gap:0.55em;justify-content:center;margin-top:1.25em;',
                  '}',
                  '.curatorFooterLink{',
                  '  display:inline-block;padding:0.42em 1.05em;border-radius:999px;',
                  '  border:1px solid rgba(127,127,127,0.32);',
                  '  color:inherit;text-decoration:none;opacity:0.85;',
                  '  transition:opacity .15s,border-color .15s,background-color .15s;',
                  '}',
                  '.curatorFooterLink:hover{',
                  '  opacity:1;border-color:#00a4dc;background:rgba(0,164,220,0.12);',
                  '}',
                  '@media (max-width:40em){.curatorFooter{padding:1.8em 1em 2em;}}',
                  '@media (prefers-reduced-motion:reduce){.curatorFooterLink{transition:none;}}'
                ].join('');
                document.head.appendChild(s);
              }

              function build() {
                var el = document.createElement('footer');
                el.id = ID;
                el.className = 'curatorFooter';

                var inner = document.createElement('div');
                inner.className = 'curatorFooterInner';
                el.appendChild(inner);

                if (data.heading) {
                  var h = document.createElement('div');
                  h.className = 'curatorFooterHeading';
                  h.textContent = data.heading;
                  inner.appendChild(h);
                }

                if (data.text) {
                  var p = document.createElement('p');
                  p.className = 'curatorFooterText';
                  p.textContent = data.text;
                  inner.appendChild(p);
                }

                if (data.links && data.links.length) {
                  var wrap = document.createElement('div');
                  wrap.className = 'curatorFooterLinks';
                  for (var i = 0; i < data.links.length; i++) {
                    var a = document.createElement('a');
                    a.className = 'curatorFooterLink';
                    a.textContent = data.links[i].label;
                    a.href = data.links[i].url;
                    // Anything off this server opens away from the app and must not
                    // be handed a window.opener to reach back through.
                    if (a.href.indexOf('http') === 0 && a.host !== window.location.host) {
                      a.target = '_blank';
                      a.rel = 'noopener noreferrer';
                    }
                    wrap.appendChild(a);
                  }
                  inner.appendChild(wrap);
                }

                return el;
              }

              function onHome() {
                var h = (window.location.hash || '').toLowerCase();
                return h.indexOf('#/home') === 0 || h === '' || h === '#/' || h.indexOf('#!/home') === 0;
              }

              function place() {
                css();
                var el = document.getElementById(ID);
                if (!el) {
                  el = build();
                  document.body.appendChild(el);
                }

                // Drawn once, then shown or hidden as the route changes. Rebuilding
                // per navigation would flicker on every click.
                el.style.display = (!data.homeOnly || onHome()) ? '' : 'none';
              }

              function start() {
                place();
                window.addEventListener('hashchange', place);
                // The client swaps view content without touching the hash in some
                // flows, so a slow re-check catches what hashchange misses. Cheap:
                // it only toggles a style.
                window.setInterval(place, 2000);
              }

              if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', start);
              } else {
                start();
              }
            })();
            """;
    }
}
