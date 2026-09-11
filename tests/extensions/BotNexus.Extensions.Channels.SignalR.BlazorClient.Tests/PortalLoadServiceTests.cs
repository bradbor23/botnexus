using System.Net;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class PortalLoadServiceTests
{
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly IGatewayEventHandler _eventHandler = Substitute.For<IGatewayEventHandler>();
    private readonly GatewayHubConnection _hub = new();
    private readonly PortalLoadService _service;

    public PortalLoadServiceTests()
    {
        // Off the network (#145). The REST client is a substitute, but the hub is real and
        // InitializeAsync connects it, so without this the fixture opens a socket to its own hub
        // URL and the outcome depends on what is listening on that port on the build host.
        _hub.TestHttpHandler = OfflineTestHttp.Handler();
        _service = new PortalLoadService(_restClient, _hub, _store, _eventHandler);
    }

    /// <summary>
    /// The client kind defaults to "desktop" so a portal that never sets it keeps the
    /// historical desktop-portal behaviour (#1209 AC#5).
    /// </summary>
    [Fact]
    public void ClientKind_DefaultsToDesktop()
    {
        _service.ClientKind.ShouldBe("desktop");
    }

    /// <summary>
    /// The client kind is settable so the per-app caller (desktop portal vs mobile app) can
    /// declare its device class before InitializeAsync forwards it to the hub connection (#1209 AC#1).
    /// </summary>
    [Fact]
    public void ClientKind_IsSettable()
    {
        _service.ClientKind = "mobile";
        _service.ClientKind.ShouldBe("mobile");
    }

    /// <summary>
    /// A stale cron-session projection whose backing session returns 404 from
    /// <c>GetSessionHistoryAsync</c> must not abort portal initialization, and must not be left in
    /// the sidebar as a row that can never be opened.
    /// <para>
    /// The projection is staged explicitly (#156). It used to be created by the <c>cron:</c>
    /// session-id prefix inference, which #2305 deleted - after which nothing created one, so this
    /// test stopped reaching the path it names and passed for four releases on an assertion that
    /// was trivially true. <c>UpsertAgent</c> merges rather than replacing, and the conversation
    /// reconcile skips <c>IsLocallySynthesised</c> entries, so a projection seeded here survives
    /// both the REST agent seed and an empty REST conversation list.
    /// </para>
    /// </summary>
    [Fact]
    public async Task InitializeAsync_stale_cron_session_history_404_does_not_abort_initialization()
    {
        // Arrange: one agent with a cron session that will 404 on history
        var staleCronSessionId = "cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54";

        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "Test Agent")]);

        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>());

        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPageDto(
            [
                new(
                    SessionId: staleCronSessionId,
                    AgentId: "agent-1",
                    ChannelType: "cron",
                    SessionType: "cron",
                    Status: "Active",
                    MessageCount: 0,
                    CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
                    UpdatedAt: DateTimeOffset.UtcNow.AddDays(-1))
            ], TotalCount: 1, HasMore: false));

        // The key: session history returns 404 for the deleted cron session
        _restClient.GetSessionHistoryAsync(staleCronSessionId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<SessionHistoryResponseDto?>(_ =>
                throw new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        // The hub connect fails by design: the fixture's TestHttpHandler refuses it without a
        // socket, so LoadError is always set and always says so. Before that, this test depended on
        // the hub failing with a message that happened to contain no "404" - which held only while
        // nothing was listening on port 5000 of the build host.

        // Stage the stale projection the scenario is named for. This is what makes the test real:
        // it is selected during init, its history is fetched from the SESSION endpoint because it is
        // synthesised, and that fetch is the stubbed 404 above.
        var cronConversationId = $"cron-session:{staleCronSessionId}";
        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Test Agent" });
        var staleAgent = _store.GetAgent("agent-1")!;
        staleAgent.Conversations[cronConversationId] = new ConversationState
        {
            ConversationId = cronConversationId,
            Title = "Scheduled run",
            IsLocallySynthesised = true,
            ActiveSessionId = staleCronSessionId,
        };

        // Act
        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        // Assert: the service should not have a 404-related error.
        // It may have a hub connection error (since we're not mocking the real hub),
        // but the critical thing is the 404 did NOT abort initialization.
        // The stale cron projection should have been removed from the store.
        var agent = _store.GetAgent("agent-1");
        Assert.NotNull(agent);

        // The stale virtual cron conversation must be removed after 404
        var cronConvId = $"cron-session:{staleCronSessionId}";
        Assert.False(agent.Conversations.ContainsKey(cronConvId),
            "Stale cron-session projection should be removed after 404 - it can never be opened.");
        // Selection must not dangle at the conversation that was just dropped.
        Assert.NotEqual(cronConvId, agent.ActiveConversationId);

        // The history 404 must not have reached the top-level catch. LoadError is set here because
        // the offline handler refuses the hub connect, which is expected and is NOT this 404 - so
        // the check is that no 404 is in it, now that the only other candidate is deterministic.
        Assert.NotNull(_service.LoadError);
        Assert.DoesNotContain("404", _service.LoadError);
        Assert.DoesNotContain("Not Found", _service.LoadError);
        Assert.Contains("offline by design", _service.LoadError);

        // The 404 really fired. Without this the test could go green again by never reaching the
        // path, which is exactly how it failed silently between #2305 and #156.
        await _restClient.Received().GetSessionHistoryAsync(
            staleCronSessionId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Ensures that a real (non-virtual) conversation 404 during initial history load
    /// is also handled gracefully — logged but not fatal to initialization.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_real_conversation_history_404_does_not_abort_initialization()
    {
        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "Test Agent")]);

        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>
            {
                new("conv-1", "agent-1", "Chat", true, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            });

        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPageDto([], 0, false));

        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ConversationHistoryResponseDto?>(_ =>
                throw new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        // Act
        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        // Assert: the conversation still exists (not removed — only virtual crons get removed)
        var agent = _store.GetAgent("agent-1");
        Assert.NotNull(agent);
        Assert.True(agent.Conversations.ContainsKey("conv-1"));

        // Same as the cron case: the only thing that may reach the top-level catch is the refused
        // hub connect, so a 404 in LoadError can only be the history one leaking.
        Assert.NotNull(_service.LoadError);
        Assert.DoesNotContain("404", _service.LoadError);
        Assert.DoesNotContain("Not Found", _service.LoadError);
        Assert.Contains("offline by design", _service.LoadError);

        await _restClient.Received().GetHistoryAsync(
            "conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression for the agent-description-not-showing bug: the REST seed path in
    /// <see cref="PortalLoadService.InitializeAsync(string, System.Threading.CancellationToken)"/>
    /// must copy <c>Description</c> from the agent summary into <c>AgentState</c>. The
    /// agent panel header renders <c>AgentState.Description</c>, and the initial portal
    /// load seeds from REST (not the SignalR broadcast), so omitting it left the header
    /// description blank even when the agent had one in config.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_SeedsAgentDescriptionFromRestSummary()
    {
        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "Beacon", Emoji: "\U0001F4E1", Description: "Signals and situational awareness", IsBuiltIn: false)]);
        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new List<ConversationSummaryDto>());
        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPageDto([], 0, false));

        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        var agent = _store.GetAgent("agent-1");
        Assert.NotNull(agent);
        Assert.Equal("Signals and situational awareness", agent.Description);
        Assert.True(agent.IsBuiltIn == false);
    }

    [Fact]
    public async Task InitializeAsync_RefreshesStoreFromApiInsteadOfUsingPreexistingConversationState()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Stale Agent",
            IsConnected = true
        });
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                "stale-conv",
                "agent-1",
                "Stale conversation",
                true,
                "Active",
                null,
                0,
                DateTimeOffset.UtcNow.AddHours(-2),
                DateTimeOffset.UtcNow.AddHours(-1))
        ]);

        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "General Assistant")]);
        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns([
                new ConversationSummaryDto(
                    "fresh-conv",
                    "agent-1",
                    "Fresh conversation",
                    true,
                    "Active",
                    "s-fresh",
                    0,
                    DateTimeOffset.UtcNow.AddMinutes(-15),
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ]);
        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPageDto([], 0, false));
        _restClient.GetHistoryAsync("fresh-conv", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationHistoryResponseDto("fresh-conv", 0, 0, 200, []));

        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        await _restClient.Received(1).GetConversationsAsync("agent-1", Arg.Any<CancellationToken>());
        await _restClient.Received(1).GetHistoryAsync("fresh-conv", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        var agent = _store.GetAgent("agent-1");
        Assert.NotNull(agent);
        Assert.True(agent.Conversations.ContainsKey("fresh-conv"));
        Assert.False(agent.Conversations.ContainsKey("stale-conv"));
    }

    /// <summary>
    /// #1838: ResumeAsync is a no-op that reports <see cref="HubResumeOutcome.Skipped"/> before the
    /// initial load has established a hub URL. There is no live connection to probe or rebuild yet,
    /// so a stray resume (e.g. a visibility event that races startup) must not attempt a reset.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_BeforeInitialize_ReturnsSkipped()
    {
        var outcome = await _service.ResumeAsync();
        outcome.ShouldBe(HubResumeOutcome.Skipped);
    }
}
