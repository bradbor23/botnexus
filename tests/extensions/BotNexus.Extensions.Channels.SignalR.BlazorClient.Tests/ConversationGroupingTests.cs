using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using System.Net;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ConversationGroupingTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly IAgentInteractionService _interaction;
    private readonly IPortalLoadService _portalLoad;

    public ConversationGroupingTests()
    {
        _store = new ClientStateStore();
        _interaction = Substitute.For<IAgentInteractionService>();
        _portalLoad = Substitute.For<IPortalLoadService>();

        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);

        var hub = OfflineTestHub.Create();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = OfflineTestHttp.Create("http://localhost/");
        var gatewayInfo = new GatewayInfoService(http, restClient);

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        var mockPrefs = Substitute.For<IPortalPreferencesService>();
        mockPrefs.Current.Returns(new PortalPreferences());
        _ctx.Services.AddSingleton(mockPrefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(new ToolsApiClient(http));
        _ctx.Services.AddStubNavOrderApiClient();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout() =>
        _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (RenderFragment)(_ => { })));

    private void SeedAgentWithConversations(params ConversationSummaryDto[] conversations)
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", conversations);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
    }

    /// <summary>
    /// #2305: a server-stamped <c>Source=Cron</c> conversation stays in the sidebar's own list.
    /// The Scheduled group it used to sit in is gone - see the toolbar schedules panel.
    /// </summary>
    [Fact]
    public void CronConversations_StayInTheConversationsList()
    {
        // Arrange: one server-stamped Source=Cron conversation (#2305: no id-prefix inference)
        SeedAgentWithConversations(
            new ConversationSummaryDto("conv-daily-check", "a-1", "Daily Check", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Cron"),
            new ConversationSummaryDto("c-1", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // The Scheduled group is gone - its replacement is the toolbar schedules panel, which lists
        // cron JOBS rather than only the ones that produced a conversation. A cron conversation is
        // still a conversation, so it must remain reachable in the sidebar's own list; keeping the
        // old subtraction after removing the group would have hidden it from both places.
        cut.WaitForAssertion(() =>
            Assert.Contains("Daily Check", cut.Find("[data-testid='conversation-group-conversations']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='conversation-group-scheduled']"));
    }

    /// <summary>
    /// #2305: the typed origin is the only mechanism that marks a conversation as cron - this
    /// replaces the deleted mutable virtual-session flag/kind fixture. It no longer moves the row
    /// to a group of its own, but it must still be the thing that classifies it.
    /// </summary>
    [Fact]
    public void CronConversations_TypedSource_IsClassifiedFromTheTypedOriginAlone()
    {
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Cron Task", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Cron"),
            new ConversationSummaryDto("c-2", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );
        Assert.Equal(ConversationSource.Cron, _store.GetAgent("a-1")!.Conversations["c-1"].Source);

        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
            Assert.Contains("Cron Task", cut.Find("[data-testid='conversation-group-conversations']").TextContent));
    }

    /// <summary>
    /// #2304: a conversation the SERVER marked <c>source="Cron"</c> is badged from the typed
    /// projection alone - no conversation-id prefix, no mutable virtual-session flag, no cron-job
    /// id lookup. The badge is what survived the group's removal.
    /// </summary>
    [Fact]
    public void ServerSuppliedCronSource_IsBadgedInTheConversationsList()
    {
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Scheduled Run", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Cron"),
            new ConversationSummaryDto("c-2", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // The BADGE is the part that matters and is unchanged: the typed projection alone marks the
        // row as Cron. Only its location moved, from the removed Scheduled group into the list.
        cut.WaitForAssertion(() =>
        {
            var convsGroup = cut.Find("[data-testid='conversation-group-conversations']");
            Assert.Contains("Scheduled Run", convsGroup.TextContent);
            Assert.Contains("Cron", convsGroup.TextContent);
            Assert.Contains("Normal Chat", convsGroup.TextContent);
        });
    }

    /// <summary>
    /// #2304: a webhook-originated conversation is badged from the immutable server signal.
    /// </summary>
    [Fact]
    public void ServerSuppliedWebhookSource_IsBadgedFromTheImmutableSignal()
    {
        // #2122 gave webhook runs their own Automated section, which - like Scheduled - starts
        // collapsed. Seed it expanded so the badged item is actually rendered. Every assertion
        // below is unchanged; only the group's visibility fixture is seeded.
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "botnexus-webhook-collapsed").SetResult("false");

        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Webhook Run", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Webhook"),
            new ConversationSummaryDto("c-2", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        var item = cut.Find("[data-conversation-id='c-1']");
        Assert.Contains("Webhook", item.TextContent);

        var normalItem = cut.Find("[data-conversation-id='c-2']");
        Assert.DoesNotContain("Webhook", normalItem.TextContent);
        Assert.DoesNotContain("Cron", normalItem.TextContent);
        Assert.DoesNotContain("Read-only", normalItem.TextContent);
    }

    /// <summary>
    /// #2304 regression guard, the conversation-shaped twin of #2248: an inbound sub-agent event
    /// must not be able to badge, regroup, or hide the user's own conversation.
    /// </summary>
    [Fact]
    public void InboundSubAgentEvent_LeavesTheUserConversationUnbadgedAndVisible()
    {
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "My Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        // Inbound, non-user-driven events fire against the same agent/conversation.
        _store.MarkSubAgent("a-1-sub");
        _store.RegisterSession("a-1", "sess-sub", sessionType: "agent-subagent", conversationId: "c-1");

        var cut = RenderLayout();

        var convsGroup = cut.Find("[data-testid='conversation-group-conversations']");
        Assert.Contains("My Chat", convsGroup.TextContent);

        var item = cut.Find("[data-conversation-id='c-1']");
        Assert.DoesNotContain("Read-only", item.TextContent);
        Assert.DoesNotContain("Virtual", item.TextContent);

        Assert.Equal(ConversationSource.Channel, _store.GetConversation("c-1")!.Source);
    }

    /// <summary>
    /// The Scheduled group's collapsed-by-default behaviour carried over to its replacement: the
    /// toolbar schedules panel is closed until the chevron is used, so the nav still starts tight.
    /// </summary>
    [Fact]
    public void SchedulesPanel_ClosedByDefault_AndOpensOnTheChevron()
    {
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // FindAll, not Find, for every read of the toggle. A `Find` whose selector was already
        // resolved before the click hands back a wrapper that reports the PRE-click attribute -
        // this test failed on a genuinely-open panel until the reads were made fresh queries.
        Assert.Equal("false", cut.FindAll("[data-testid='schedules-toggle']")[0].GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[data-testid='schedules-menu']"));

        cut.FindAll("[data-testid='schedules-toggle']")[0].Click();

        Assert.Equal("true", cut.FindAll("[data-testid='schedules-toggle']")[0].GetAttribute("aria-expanded"));
        Assert.Single(cut.FindAll("[data-testid='schedules-menu']"));

        // And it closes again on the same control, so the chevron is a toggle and not a one-way door.
        cut.FindAll("[data-testid='schedules-toggle']")[0].Click();

        Assert.Equal("false", cut.FindAll("[data-testid='schedules-toggle']")[0].GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[data-testid='schedules-menu']"));
    }

    /// <summary>
    /// Narrow enough and Cron Jobs moves into the "More" menu. There it must degrade to a plain
    /// link: a panel nested inside a panel has nowhere to open, so the split control is dropped and
    /// the entry becomes an ordinary anchor to /cron. This is the branch that keeps the feature from
    /// making the entry unreachable on a narrow window.
    /// </summary>
    [Fact]
    public async Task SchedulesPanel_DegradesToAPlainLink_InsideTheOverflowMenu()
    {
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();
        Assert.Single(cut.FindAll("[data-testid='schedules-dropdown']"));

        // What the measurer would report on a narrow window. One item fits, so everything after the
        // first - Cron Jobs included - is pushed into the menu.
        await cut.InvokeAsync(() => cut.Instance.SetVisibleNavCount(1));

        cut.FindAll("[data-testid='toolbar-more']")[0].Click();

        var menu = cut.FindAll("[data-testid='toolbar-overflow-menu']")[0];
        var cron = menu.QuerySelector("[data-testid='nav-cron-jobs']");
        Assert.NotNull(cron);
        Assert.Equal("cron", cron!.GetAttribute("href"));

        // No split control and no panel anywhere on the page while the entry lives in the menu.
        Assert.Empty(cut.FindAll("[data-testid='schedules-dropdown']"));
        Assert.Empty(cut.FindAll("[data-testid='schedules-toggle']"));
        Assert.Empty(cut.FindAll("[data-testid='schedules-menu']"));
    }

    /// <summary>
    /// The count moved from cron CONVERSATIONS to cron JOBS, which is the substance of the change:
    /// the old group could only count schedules that had already run. A job with no conversation
    /// must appear here, so this seeds the cron API rather than the conversation store.
    /// </summary>
    [Fact]
    public void SchedulesPanel_ListsEveryJob_IncludingOnesThatHaveNeverRun()
    {
        var handler = new MockCronHttpHandler();
        handler.SetCronResponse("""
            [
              {"id":"j-1","name":"Nightly Digest","schedule":"0 4 * * *","enabled":true,"conversationId":"conv-a","agentId":"a-1"},
              {"id":"j-2","name":"Never Run Yet","schedule":"0 */2 * * *","enabled":true},
              {"id":"j-3","name":"Switched Off","schedule":"0 9 * * 1","enabled":false}
            ]
            """);
        var (ctx, store) = BuildCronLayoutContext(handler);
        using var _panelCtx = ctx;

        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.SeedConversations("a-1", [
            new ConversationSummaryDto("conv-a", "a-1", "Digest Conv", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = ctx.Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(_ => { })));

        // The job list arrives asynchronously in OnAfterRenderAsync, so the panel is opened inside
        // the retry rather than once before it.
        cut.WaitForAssertion(() =>
        {
            if (cut.FindAll("[data-testid='schedules-menu']").Count == 0)
                cut.Find("[data-testid='schedules-toggle']").Click();

            var rows = cut.FindAll("[data-testid='schedule-row']");
            Assert.Equal(3, rows.Count);
        });

        var menu = cut.Find("[data-testid='schedules-menu']");
        Assert.Contains("Nightly Digest", menu.TextContent);
        Assert.Contains("Never Run Yet", menu.TextContent);   // the case the old group could not show
        Assert.Contains("0 */2 * * *", menu.TextContent);      // the schedule itself is the identity
        Assert.Single(cut.FindAll("[data-testid='schedule-disabled']"));
        Assert.Single(cut.FindAll("[data-testid='schedules-view-all']"));
    }

    [Fact]
    public void PinnedGroup_OnlyShownWhenPinnedExist()
    {
        // Arrange: no pinned conversations
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // Pinned group should NOT be rendered
        Assert.Empty(cut.FindAll("[data-testid='conversation-group-pinned']"));

        // Conversations group should still be present
        cut.Find("[data-testid='conversation-group-conversations']");
    }

    [Fact]
    public void PinnedConversations_RenderedInPinnedGroup()
    {
        // Arrange: one pinned conversation
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Important Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsPinned: true),
            new ConversationSummaryDto("c-2", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // Pinned group should be rendered
        var pinnedGroup = cut.Find("[data-testid='conversation-group-pinned']");
        Assert.Contains("Important Chat", pinnedGroup.TextContent);

        // Normal Chat should NOT be in the pinned group
        Assert.DoesNotContain("Normal Chat", pinnedGroup.TextContent);
    }

    [Fact]
    public void PinButton_Click_InvokesSetConversationPinned()
    {
        // Arrange: an unpinned normal conversation exposes a pin affordance.
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Pin Target", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );

        var cut = RenderLayout();

        // Wrap BOTH the Find and the Click inside a single InvokeAsync so no re-render can slip
        // between locating the button and dispatching its click — that gap is what invalidated the
        // event-handler id (bUnit UnknownEventHandlerIdException) and flaked this suite across PRs.
        cut.InvokeAsync(() => cut.Find("[data-testid='conversation-pin-btn']").Click());

        // Clicking pins the conversation via the interaction service (pinned: true).
        // InvokeAsync dispatches the click but returns a Task we don't await, so the async click
        // handler may not have completed when we verify the mock. WaitForAssertion retries the
        // Received(1) check until the async handler settles (or times out), eliminating the
        // received-call race that flaked this test under parallel CI load.
        cut.WaitForAssertion(() => _interaction.Received(1).SetConversationPinnedAsync("a-1", "c-1", true));
    }

    [Fact]
    public void PinButton_Click_OnPinnedConversation_Unpins()
    {
        // Arrange: an already-pinned conversation's pin button toggles it back off.
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Pinned Target", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsPinned: true)
        );

        var cut = RenderLayout();

        // Wrap BOTH the Find and the Click inside a single InvokeAsync so no re-render can slip
        // between locating the button and dispatching its click — the captured handler id would
        // otherwise go stale after an optimistic re-render (bUnit UnknownEventHandlerIdException),
        // which surfaced the stale-handler exception across multiple PRs under parallel CI load.
        cut.InvokeAsync(() => cut.Find("[data-testid='conversation-pin-btn']").Click());

        // InvokeAsync dispatches the click but returns a Task we don't await, so the async click
        // handler may not have completed when we verify the mock. WaitForAssertion retries the
        // Received(1) check until the async handler settles (or times out), eliminating the
        // received-call race that flaked this test under parallel CI load.
        cut.WaitForAssertion(() => _interaction.Received(1).SetConversationPinnedAsync("a-1", "c-1", false));
    }

    [Fact]
    public void NormalConversations_RenderedInConversationsGroup()
    {
        // Arrange: mix of pinned, normal, and Source=Cron conversations
        SeedAgentWithConversations(
            new ConversationSummaryDto("c-1", "a-1", "Pinned Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsPinned: true),
            new ConversationSummaryDto("c-2", "a-1", "Normal Chat", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("conv-job-1", "a-1", "Cron Job", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Cron")
        );

        var cut = RenderLayout();

        // Normal conversations group should have "Normal Chat"
        var convsGroup = cut.Find("[data-testid='conversation-group-conversations']");
        Assert.Contains("Normal Chat", convsGroup.TextContent);

        // Pinned should NOT be in the normal conversations group - that rule is unchanged.
        Assert.DoesNotContain("Pinned Chat", convsGroup.TextContent);

        // Cron SHOULD be here now, badged. This is the deliberate inversion: the Scheduled group
        // moved to the toolbar schedules panel, which lists cron JOBS, and a cron conversation is
        // still a conversation. Had the old subtraction stayed, it would have been in no group at
        // all - visible in neither the sidebar nor the panel.
        Assert.Contains("Cron Job", convsGroup.TextContent);
        Assert.Contains("Cron", convsGroup.TextContent);
    }

    /// <summary>
    /// A conversation a cron job points at stays reachable, and the job itself is named in the
    /// toolbar panel. Previously this asserted the conversation appeared in the Scheduled group.
    /// </summary>
    [Fact]
    public void ConversationAssignedToCronJob_StaysInTheListAndTheJobIsNamedInThePanel()
    {
        // Arrange: set up a mock HTTP handler that returns cron jobs with a conversationId
        var handler = new MockCronHttpHandler();
        handler.SetCronResponse("[{\"id\":\"job-1\",\"name\":\"Daily Digest\",\"schedule\":\"0 8 * * *\",\"enabled\":true,\"conversationId\":\"conv:assigned-to-cron\"}]");
        var (ctx, store) = BuildCronLayoutContext(handler);
        using var _ctxScope = ctx;

        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.SeedConversations("a-1", [
            new ConversationSummaryDto("conv:assigned-to-cron", "a-1", "Digest Conv", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("conv:normal", "a-1", "Normal Conv", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (RenderFragment)(_ => { })));

        // Both conversations sit in the one list now; neither is pulled into a separate group.
        var convsGroup = cut.Find("[data-testid='conversation-group-conversations']");
        Assert.Contains("Digest Conv", convsGroup.TextContent);
        Assert.Contains("Normal Conv", convsGroup.TextContent);

        // The job list arrives asynchronously in OnAfterRenderAsync (LoadCronJobsAsync), so the
        // panel is opened inside the retry rather than once before it.
        cut.WaitForAssertion(() =>
        {
            if (cut.FindAll("[data-testid='schedules-menu']").Count == 0)
                cut.Find("[data-testid='schedules-toggle']").Click();

            Assert.Contains("Daily Digest", cut.Find("[data-testid='schedules-menu']").TextContent);
        });
    }

    /// <summary>
    /// The layout's full service graph with a mocked cron API. Extracted because two tests need it:
    /// the conversation-assignment test that already had it inline, and the schedules-panel test
    /// that needs jobs with no conversation - the case the removed sidebar group could never show.
    /// </summary>
    private static (BunitContext Ctx, ClientStateStore Store) BuildCronLayoutContext(MockCronHttpHandler handler)
    {
        var ctx = new BunitContext();
        var httpWithMock = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var store = new ClientStateStore();
        ctx.Services.AddSingleton<IClientStateStore>(store);
        ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);
        ctx.Services.AddSingleton(portalLoad);
        ctx.Services.AddSingleton(OfflineTestHub.Create());
        ctx.Services.AddSingleton(new GatewayInfoService(httpWithMock, restClient));
        ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        var mockPrefs = Substitute.For<IPortalPreferencesService>();
        mockPrefs.Current.Returns(new PortalPreferences());
        ctx.Services.AddSingleton(mockPrefs);
        ctx.Services.AddSingleton(restClient);
        ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        ctx.Services.AddSingleton(httpWithMock);
        ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        ctx.Services.AddSingleton(new CronApiClient(httpWithMock));
        ctx.Services.AddSingleton(new ToolsApiClient(httpWithMock));
        ctx.Services.AddStubNavOrderApiClient();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return (ctx, store);
    }

    private sealed class MockCronHttpHandler : HttpMessageHandler
    {
        private string _cronJson = "[]";

        public void SetCronResponse(string json) => _cronJson = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.Contains("/api/cron", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(_cronJson, System.Text.Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}