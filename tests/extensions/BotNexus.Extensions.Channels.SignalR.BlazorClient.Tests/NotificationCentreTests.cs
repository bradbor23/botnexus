using System.Net;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the notification centre in the banner.
/// </summary>
/// <remarks>
/// The load-bearing test is the FIRST one. This component sits in the banner, which renders on
/// every page, so a version that threw on a missing service took the whole layout down with it -
/// 163 tests failed against a baseline of 16 on the first draft, and deploying it would have left
/// the portal rendering nothing at all rather than merely lacking a bell. Everything else here is
/// ordinary behaviour; that one is the regression guard.
/// </remarks>
public sealed class NotificationCentreTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly StubHandler _handler = new();

    public NotificationCentreTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(new GatewayHubConnection());
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>Registers the API client. Omitted deliberately by the degradation test.</summary>
    private void WithApi() =>
        _ctx.Services.AddSingleton(new NotificationsApiClient(
            new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") }));

    private IRenderedComponent<NotificationCentre> Render() => _ctx.Render<NotificationCentre>();

    private static string Item(string id, string title, bool unread = true, string? link = null) =>
        "{" + $"\"id\":\"{id}\",\"kind\":\"AgentRunFailed\",\"severity\":\"Error\","
        + $"\"title\":\"{title}\",\"createdAtUtc\":\"2026-08-28T10:00:00Z\","
        + $"\"readAtUtc\":{(unread ? "null" : "\"2026-08-28T10:05:00Z\"")}"
        + (link is null ? "" : $",\"link\":\"{link}\"") + "}";

    // THE regression guard: the banner is on every page.
    [Fact]
    public void Renders_nothing_when_the_notifications_client_is_unavailable()
    {
        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid='notification-centre']"));
        Assert.Empty(cut.FindAll("[data-testid='notification-bell']"));
    }

    [Fact]
    public void Renders_the_bell_when_the_client_is_available()
    {
        WithApi();
        _handler.UnreadCount = 0;

        var cut = Render();

        Assert.Single(cut.FindAll("[data-testid='notification-bell']"));
    }

    // The badge is the whole point of the bell, and it must be right before the panel is ever
    // opened - it covers what happened while this browser was closed or on another device.
    [Fact]
    public void Shows_the_unread_count_without_opening_the_panel()
    {
        WithApi();
        _handler.UnreadCount = 3;

        var cut = Render();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-badge']").Count > 0);
        Assert.Equal("3", cut.Find("[data-testid='notification-badge']").TextContent.Trim());
    }

    // A badge that renders "0" would nag about nothing.
    [Fact]
    public void Shows_no_badge_when_nothing_is_unread()
    {
        WithApi();
        _handler.UnreadCount = 0;

        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid='notification-badge']"));
    }

    [Fact]
    public void Caps_a_large_badge_so_it_stays_readable()
    {
        WithApi();
        _handler.UnreadCount = 250;

        var cut = Render();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-badge']").Count > 0);
        Assert.Equal("99+", cut.Find("[data-testid='notification-badge']").TextContent.Trim());
    }

    [Fact]
    public void Opening_lists_the_notifications()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Agent run failed") + "," + Item("b", "Job failed") + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 2);
        Assert.Contains("Agent run failed", cut.Markup);
    }

    // An empty feed is the ordinary state of a healthy gateway, so it must read as reassurance
    // rather than as an error.
    [Fact]
    public void Opening_an_empty_feed_explains_what_would_appear()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-empty']").Count > 0);
        Assert.Contains("Nothing to report", cut.Find("[data-testid='notification-empty']").TextContent);
    }

    [Fact]
    public void Unread_items_are_marked_as_such_for_styling()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Unread one") + "," + Item("b", "Read one", unread: false) + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 2);

        var items = cut.FindAll("[data-testid='notification-item']");
        Assert.Equal("true", items[0].GetAttribute("data-unread"));
        Assert.Equal("false", items[1].GetAttribute("data-unread"));
    }

    [Fact]
    public void Dismissing_removes_the_item_from_the_list()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Doomed") + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 1);

        cut.Find("[data-testid='notification-dismiss']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 0);
        Assert.Contains("DELETE /api/notifications/a", _handler.Calls);
    }

    [Fact]
    public void Mark_all_read_is_offered_only_when_something_is_unread()
    {
        WithApi();
        _handler.UnreadCount = 0;
        _handler.ListJson = "[" + Item("a", "Read one", unread: false) + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 1);

        Assert.Empty(cut.FindAll("[data-testid='notification-mark-all']"));
    }

    [Fact]
    public void Mark_all_read_calls_the_gateway()
    {
        WithApi();
        _handler.UnreadCount = 2;
        _handler.ListJson = "[" + Item("a", "One") + "," + Item("b", "Two") + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-mark-all']").Count > 0);

        cut.Find("[data-testid='notification-mark-all']").Click();

        cut.WaitForState(() => _handler.Calls.Contains("POST /api/notifications/read-all"));
    }

    // Opening an item IS the act of having seen it; a separate gesture is one nobody would make.
    [Fact]
    public void Opening_an_unread_item_marks_it_read()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Unread one") + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 1);

        cut.Find(".notification-item-main").Click();

        cut.WaitForState(() => _handler.Calls.Contains("POST /api/notifications/a/read"));
    }

    // Closing must not leave a stale panel behind.
    [Fact]
    public void The_panel_closes_again()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-panel']").Count > 0);

        cut.Find("[data-testid='notification-close']").Click();

        Assert.Empty(cut.FindAll("[data-testid='notification-panel']"));
    }

    // The bug this guards: the panel was a child of the bell, and .banner-header clips its own
    // overflow, so the list was sliced off at the bottom edge of the top bar and appeared to sit
    // behind the page. It has to hang from the fixed overlay, outside the clipped bar, instead.
    [Fact]
    public void The_panel_hangs_outside_the_clipped_banner()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-panel']").Count > 0);

        var bell = cut.Find("[data-testid='notification-centre']");
        Assert.Null(bell.QuerySelector("[data-testid='notification-panel']"));
        Assert.NotNull(cut.Find(".notification-overlay [data-testid='notification-panel']"));
    }

    // The overlay covers the viewport so a click anywhere dismisses the panel; the panel itself
    // must not close when clicked, or the list would vanish on the way to an item.
    [Fact]
    public void Clicking_away_closes_the_panel_but_clicking_it_does_not()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Agent run failed", unread: false) + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 1);

        // Reading an item is a click INSIDE the panel: it must not reach the overlay behind it.
        cut.Find("[data-testid='notification-item'] .notification-item-main").Click();
        Assert.Single(cut.FindAll("[data-testid='notification-panel']"));

        cut.Find(".notification-overlay").Click();
        Assert.Empty(cut.FindAll("[data-testid='notification-panel']"));
    }

    /// <summary>Answers the notification endpoints and records what was called.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string ListJson { get; set; } = "[]";

        public int UnreadCount { get; set; }

        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Calls.Add($"{request.Method.Method} {path}");

            if (path.EndsWith("/unread-count", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"count\":" + UnreadCount + "}"));
            }

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Json(ListJson));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
