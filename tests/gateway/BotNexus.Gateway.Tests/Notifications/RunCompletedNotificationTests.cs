using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Core.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgentUserMessage = BotNexus.Gateway.Abstractions.Models.AgentUserMessage;

namespace BotNexus.Gateway.Tests.Notifications;

/// <summary>
/// Pins when a finished reply is worth telling someone about (#168).
/// </summary>
/// <remarks>
/// Until now a run that ended well raised nothing, deliberately: in a conversation someone is
/// watching, every message is a run and a notification would announce a reply already on screen.
/// A phone is the case that reasoning did not cover — the person is not watching, and the reply is
/// exactly what they are waiting to hear about. So the event is raised, and the cases where it
/// would be noise are excluded: a chat whose replies already reach Telegram, two agents talking to
/// each other, and the silences (NO_REPLY, a heartbeat) that are not replies at all.
/// </remarks>
public sealed class RunCompletedNotificationTests
{
    private const string ThreadId = "conv-1";

    [Fact]
    public async Task A_finished_reply_is_raised_with_the_agent_and_conversation()
    {
        var publisher = new RecordingPublisher();
        await using var host = Host(publisher, "Hello, world!");

        await host.DispatchAsync(Message());

        var raised = Assert.Single(publisher.Published, n => n.Kind == NotificationKind.AgentRunCompleted);
        Assert.Equal("agent-a", raised.AgentId);
        Assert.Equal(ThreadId, raised.ConversationId);
        Assert.Equal(NotificationSeverity.Info, raised.Severity);
        Assert.Equal($"agent/agent-a/conversation/{ThreadId}", raised.Link);
    }

    // A silence is not a reply, and neither is a heartbeat: both would file a notification for a
    // turn that said nothing to anyone.
    [Theory]
    [InlineData("NO_REPLY")]
    [InlineData("HEARTBEAT_OK")]
    public async Task A_silence_raises_nothing(string content)
    {
        var publisher = new RecordingPublisher();
        await using var host = Host(publisher, content);

        await host.DispatchAsync(Message());

        Assert.DoesNotContain(publisher.Published, n => n.Kind == NotificationKind.AgentRunCompleted);
    }

    // Telegram already puts the reply in front of the person. A second alert for the same words is
    // the noise this level was supposed to avoid.
    [Fact]
    public async Task A_reply_that_arrived_over_telegram_raises_nothing()
    {
        var publisher = new RecordingPublisher();
        await using var host = Host(publisher, "Hello, world!", channelType: "telegram");

        await host.DispatchAsync(Message(channelType: "telegram"));

        Assert.DoesNotContain(publisher.Published, n => n.Kind == NotificationKind.AgentRunCompleted);
    }

    // The same holds for a chat started elsewhere that is bound outward to Telegram: the reply
    // reaches Telegram whichever side began it.
    [Fact]
    public async Task A_conversation_bound_to_telegram_raises_nothing()
    {
        var publisher = new RecordingPublisher();
        await using var host = Host(
            publisher,
            "Hello, world!",
            bindings: [new ChannelBinding { ChannelType = ChannelKey.From("telegram") }]);

        await host.DispatchAsync(Message());

        Assert.DoesNotContain(publisher.Published, n => n.Kind == NotificationKind.AgentRunCompleted);
    }

    [Fact]
    public async Task Two_agents_talking_to_each_other_raise_nothing()
    {
        var publisher = new RecordingPublisher();
        await using var host = Host(publisher, "Hello, world!", kind: ConversationKind.AgentAgent);

        await host.DispatchAsync(Message());

        Assert.DoesNotContain(publisher.Published, n => n.Kind == NotificationKind.AgentRunCompleted);
    }

    // A run that fails already reports itself; it must not also report as a finished reply.
    [Fact]
    public async Task A_failed_run_is_not_reported_as_a_finished_reply()
    {
        var publisher = new RecordingPublisher();
        await using var host = Host(publisher, "Hello, world!", promptThrows: true);

        await host.DispatchAsync(Message());

        Assert.DoesNotContain(publisher.Published, n => n.Kind == NotificationKind.AgentRunCompleted);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static GatewayHost Host(
        INotificationPublisher publisher,
        string reply,
        string channelType = "web",
        ConversationKind kind = ConversationKind.HumanAgent,
        IReadOnlyList<ChannelBinding>? bindings = null,
        bool promptThrows = false)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-a"]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
        handle.Setup(h => h.IsRunning).Returns(false);
        if (promptThrows)
        {
            handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("provider refused"));
        }
        else
        {
            handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AgentResponse { Content = reply });
        }

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
                AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-1"),
            AgentId = AgentId.From("agent-a"),
            ConversationId = ConversationId.From(ThreadId)
        };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
                SessionId.From("session-1"), AgentId.From("agent-a"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var conversations = new Mock<IConversationStore>();
        conversations.Setup(c => c.GetAsync(
                ConversationId.From(ThreadId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Conversation
            {
                ConversationId = ConversationId.From(ThreadId),
                AgentId = AgentId.From("agent-a"),
                Kind = kind,
                ChannelBindings = [.. bindings ?? []]
            });

        return new GatewayHost(
            supervisor.Object,
            router.Object,
            sessions.Object,
            new RecordingActivityBroadcaster(),
            ChannelManager(channelType),
            Mock.Of<ISessionCompactor>(),
            new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationStore: conversations.Object,
            notificationPublisher: publisher);
    }

    private static IChannelManager ChannelManager(string channelType)
    {
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(c => c.ChannelType).Returns(ChannelKey.From(channelType));
        adapter.SetupGet(c => c.DisplayName).Returns(channelType);
        adapter.SetupGet(c => c.SupportsStreaming).Returns(false);
        adapter.SetupGet(c => c.SupportsThinkingDisplay).Returns(false);
        adapter.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns([adapter.Object]);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((ChannelKey key) =>
            key.Equals(adapter.Object.ChannelType) ? adapter.Object : null);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((ChannelKey key, string? _) =>
            key.Equals(adapter.Object.ChannelType) ? adapter.Object : null);

        return manager.Object;
    }

    private static InboundMessage Message(string channelType = "web")
        => new()
        {
            ChannelType = channelType,
            SenderId = "sender-1",
            Sender = CitizenId.Of(UserId.From("sender-1")),
            ChannelAddress = ChannelAddress.From(ThreadId),
            Content = "hello",
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, "session-1", ThreadId),
            Metadata = new Dictionary<string, object?>()
        };

    private sealed class RecordingPublisher : INotificationPublisher
    {
        private readonly List<Notification> _published = [];

        public IReadOnlyList<Notification> Published
        {
            get
            {
                lock (_published)
                    return [.. _published];
            }
        }

        public Task PublishAsync(Notification notification, CancellationToken ct = default)
        {
            lock (_published)
                _published.Add(notification);

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public List<GatewayActivity> Activities { get; } = [];

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);

            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<GatewayActivity> SubscribeAsync(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<GatewayActivity>();
    }
}
