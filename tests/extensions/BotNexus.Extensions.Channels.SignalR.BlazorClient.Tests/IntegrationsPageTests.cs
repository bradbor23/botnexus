using System.Net;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

using IntegrationsPage = BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages.Integrations;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the read-only Integrations page (Phase 1 of the integrations plan): catalog cards, search,
/// paging, the detail panel, and the two failure states an operator must be able to tell apart -
/// "the registry is down" versus "nothing matched".
/// </summary>
public sealed class IntegrationsPageTests : IDisposable
{
    private const string CatalogPath = "/api/integrations/catalog";

    private const string FirstPage = """
        {
          "entries": [
            {
              "name": "io.github.github/github-mcp-server",
              "title": "GitHub",
              "description": "Repos, issues and pull requests.",
              "version": "1.12.1",
              "repositoryUrl": "https://github.com/github/github-mcp-server",
              "remotes": [
                { "type": "streamable-http", "url": "https://api.githubcopilot.com/mcp/",
                  "headers": [ { "name": "Authorization", "description": "PAT", "isRequired": true, "isSecret": true } ] }
              ],
              "packages": [
                { "registryType": "oci", "identifier": "ghcr.io/github/github-mcp-server:1.12.1", "transport": "stdio",
                  "inputs": [ { "name": "token", "isRequired": false, "isSecret": true } ] }
              ],
              "status": "active",
              "needsCredentials": true
            },
            {
              "name": "agency.kesey/pretrip",
              "title": null,
              "description": "Compliance scanner.",
              "version": "1.0.1",
              "repositoryUrl": "javascript:alert(1)",
              "remotes": [],
              "packages": [ { "registryType": "npm", "identifier": "pretrip-mcp", "transport": "stdio", "inputs": [] } ],
              "needsCredentials": false,
              "someFutureField": { "ignored": true }
            }
          ],
          "nextCursor": "agency.kesey/pretrip:1.0.1",
          "fetchedAtUtc": "2026-09-14T17:00:00Z",
          "stale": false
        }
        """;

    private readonly BunitContext _ctx = new();
    private readonly CatalogHandler _handler = new();

    public IntegrationsPageTests()
    {
        _ctx.Services.AddSingleton(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });
        _ctx.Services.AddScoped<IntegrationsApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Opening_the_page_lists_the_first_catalog_page_with_kind_and_credential_badges()
    {
        _handler.Respond(CatalogPath, FirstPage);

        var cut = _ctx.Render<IntegrationsPage>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='integration-card']").Count));
        var cards = cut.FindAll("[data-testid='integration-card']");
        Assert.Equal("io.github.github/github-mcp-server", cards[0].GetAttribute("data-server-name"));

        var titles = cut.FindAll("[data-testid='integration-title']").Select(t => t.TextContent.Trim()).ToArray();
        // An untitled server falls back to its registry name rather than rendering a blank card.
        Assert.Equal(["GitHub", "agency.kesey/pretrip"], titles);

        var kinds = cut.FindAll("[data-testid='integration-kind']").Select(k => k.TextContent.Trim()).ToArray();
        Assert.Equal(["Hosted + local package", "Local package"], kinds);

        Assert.Single(cut.FindAll("[data-testid='integration-needs-credentials']"));
        Assert.Empty(cut.FindAll("[data-testid='integrations-stale']"));
        Assert.Empty(cut.FindAll("[data-testid='integrations-error']"));
    }

    [Fact]
    public void Searching_sends_the_query_and_replaces_the_results()
    {
        _handler.Respond(CatalogPath, FirstPage);
        _handler.Respond(CatalogPath + "?search=notion", """
            { "entries": [ { "name": "com.notion/mcp", "title": "Notion", "remotes": [ { "type": "streamable-http", "url": "https://mcp.notion.com/mcp", "headers": [] } ], "packages": [] } ],
              "nextCursor": null, "fetchedAtUtc": "2026-09-14T17:00:00Z", "stale": false }
            """);

        var cut = _ctx.Render<IntegrationsPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='integration-card']").Count));

        cut.Find("[data-testid='integrations-search']").Input("  notion ");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var card = Assert.Single(cut.FindAll("[data-testid='integration-card']"));
            Assert.Equal("com.notion/mcp", card.GetAttribute("data-server-name"));
        });
        Assert.Contains(CatalogPath + "?search=notion", _handler.Requests);
        Assert.Empty(cut.FindAll("[data-testid='integrations-more']"));
    }

    [Fact]
    public void Load_more_appends_the_next_page_using_the_cursor()
    {
        _handler.Respond(CatalogPath, FirstPage);
        _handler.Respond(CatalogPath + "?cursor=agency.kesey/pretrip:1.0.1", """
            { "entries": [ { "name": "com.example/third", "remotes": [], "packages": [] } ],
              "nextCursor": null, "fetchedAtUtc": "2026-09-14T17:00:00Z", "stale": false }
            """);

        var cut = _ctx.Render<IntegrationsPage>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='integrations-more']")));

        cut.Find("[data-testid='integrations-more']").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='integration-card']").Count));
        Assert.Empty(cut.FindAll("[data-testid='integrations-more']"));
    }

    [Fact]
    public void Selecting_a_card_shows_its_endpoints_packages_and_inputs()
    {
        _handler.Respond(CatalogPath, FirstPage);

        var cut = _ctx.Render<IntegrationsPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='integration-card']").Count));
        Assert.Empty(cut.FindAll("[data-testid='integration-detail']"));

        cut.FindAll("[data-testid='integration-card'] button")[0].Click();

        var detail = cut.Find("[data-testid='integration-detail']");
        Assert.Equal("io.github.github/github-mcp-server", cut.Find("[data-testid='integration-detail-name']").TextContent);
        Assert.Contains("https://api.githubcopilot.com/mcp/", cut.Find("[data-testid='integration-remotes']").TextContent);
        Assert.Contains("ghcr.io/github/github-mcp-server:1.12.1", cut.Find("[data-testid='integration-packages']").TextContent);

        var inputs = cut.FindAll("[data-testid='integration-input']").Select(i => i.TextContent.Trim()).ToArray();
        Assert.Equal(["Header Authorization (secret, required)", "Variable token (secret)"], inputs);

        Assert.Equal("https://github.com/github/github-mcp-server", cut.Find("[data-testid='integration-repository']").GetAttribute("href"));
        Assert.NotNull(detail.QuerySelector("[data-testid='integration-install-note']"));
    }

    [Fact]
    public void A_non_http_repository_url_from_the_registry_is_never_rendered_as_a_link()
    {
        _handler.Respond(CatalogPath, FirstPage);

        var cut = _ctx.Render<IntegrationsPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='integration-card']").Count));

        cut.FindAll("[data-testid='integration-card'] button")[1].Click();

        Assert.NotNull(cut.Find("[data-testid='integration-detail']"));
        Assert.Empty(cut.FindAll("[data-testid='integration-repository']"));
        Assert.DoesNotContain("javascript:", cut.Markup);
    }

    [Fact]
    public void Stale_results_say_the_registry_was_unreachable_and_when_they_were_saved()
    {
        _handler.Respond(CatalogPath, FirstPage.Replace("\"stale\": false", "\"stale\": true"));

        var cut = _ctx.Render<IntegrationsPage>();

        cut.WaitForAssertion(() =>
            Assert.Contains("2026-09-14 17:00 UTC", cut.Find("[data-testid='integrations-stale']").TextContent));
        Assert.Equal(2, cut.FindAll("[data-testid='integration-card']").Count);
    }

    [Fact]
    public void An_unreachable_registry_shows_the_gateway_error_not_the_empty_state()
    {
        _handler.Respond(CatalogPath, """{ "error": "The MCP registry could not be reached, and nothing is cached for this request." }""", HttpStatusCode.BadGateway);

        var cut = _ctx.Render<IntegrationsPage>();

        cut.WaitForAssertion(() =>
            Assert.Contains("could not be reached", cut.Find("[data-testid='integrations-error']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='integrations-empty']"));
        Assert.Empty(cut.FindAll("[data-testid='integration-card']"));
    }

    [Fact]
    public void A_search_with_no_matches_shows_the_empty_state()
    {
        _handler.Respond(CatalogPath, """{ "entries": [], "nextCursor": null, "fetchedAtUtc": "2026-09-14T17:00:00Z", "stale": false }""");

        var cut = _ctx.Render<IntegrationsPage>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='integrations-empty']")));
        Assert.Empty(cut.FindAll("[data-testid='integrations-error']"));
    }

    /// <summary>Answers by unescaped path and query; anything unconfigured is a 404.</summary>
    private sealed class CatalogHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public void Respond(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _responses[pathAndQuery] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            Requests.Add(key);

            var (status, body) = _responses.TryGetValue(key, out var configured)
                ? configured
                : (HttpStatusCode.NotFound, "{}");

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
