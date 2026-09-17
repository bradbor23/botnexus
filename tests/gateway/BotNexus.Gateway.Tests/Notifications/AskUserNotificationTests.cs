using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Services;

namespace BotNexus.Gateway.Tests.Notifications;

/// <summary>
/// Pins the notification raised when an agent blocks on <c>ask_user</c>.
/// </summary>
/// <remarks>
/// This is the one kind where the work is genuinely stopped until a person acts, so it is the one
/// most worth reaching a phone for. It is raised once the question is saved, on the path that is
/// about to block, which is why the tests care that a failing or slow publisher cannot affect asking
/// at all.
/// </remarks>
public sealed class AskUserNotificationTests
{
    private static ConversationId Conversation(string id = "c_test") => ConversationId.From(id);

    [Fact]
    public void Announcing_a_prompt_raises_a_waiting_notification()
    {
        var publisher = new RecordingPublisher();
        var registry = new AskUserResponseRegistry(publisher);

        registry.Register(Conversation(), timeout: null);
        registry.AnnounceWaiting(Conversation(), prompt: null);

        var raised = Assert.Single(publisher.Published);
        Assert.Equal(NotificationKind.AgentWaitingForInput, raised.Kind);
        Assert.Equal(NotificationSeverity.Warning, raised.Severity);
        Assert.Equal("c_test", raised.ConversationId);
        Assert.Contains("c_test", raised.Link);
    }

    // #168: registering happens before the question is saved, and a push reads the question's
    // answers from the saved copy. Announcing is the caller's, once the save is done.
    [Fact]
    public void Registering_alone_raises_nothing()
    {
        var publisher = new RecordingPublisher();
        var registry = new AskUserResponseRegistry(publisher);

        registry.Register(Conversation(), timeout: null);

        Assert.Empty(publisher.Published);
    }

    // Telegram shows the question itself; a phone that says only "a conversation is paused" makes
    // someone open the app to find out what was even asked.
    [Fact]
    public void The_question_itself_is_what_the_notification_says()
    {
        var publisher = new RecordingPublisher();
        var registry = new AskUserResponseRegistry(publisher);

        registry.AnnounceWaiting(
            Conversation(),
            prompt: "Deploy the gateway now, or wait for the overnight window?");

        var raised = Assert.Single(publisher.Published);
        Assert.Equal("Deploy the gateway now, or wait for the overnight window?", raised.Body);
    }

    // A question with no text is still a question, and the old wording is better than a blank body.
    [Fact]
    public void A_question_with_no_text_still_says_something_useful()
    {
        var publisher = new RecordingPublisher();
        var registry = new AskUserResponseRegistry(publisher);

        registry.AnnounceWaiting(Conversation(), prompt: "   ");

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(publisher.Published).Body));
    }

    // Asking must not depend on the notification succeeding: a question cannot be allowed to fail
    // because the thing that reports the question failed.
    [Fact]
    public void A_throwing_publisher_does_not_break_asking()
    {
        var registry = new AskUserResponseRegistry(new ThrowingPublisher());

        var (requestId, task) = registry.Register(Conversation(), timeout: null);
        registry.AnnounceWaiting(Conversation(), prompt: "Deploy now?");

        Assert.NotEmpty(requestId);
        Assert.NotNull(task);
        Assert.False(task.IsCompleted);
    }

    // The registry is constructed in many places that know nothing about notifications.
    [Fact]
    public void A_registry_without_a_publisher_still_registers()
    {
        var registry = new AskUserResponseRegistry();

        var (requestId, _) = registry.Register(Conversation(), timeout: null);

        Assert.NotEmpty(requestId);
    }

    // One pending prompt per conversation is an existing invariant; raising a notification must not
    // have loosened it, and a refused registration must not report a question nobody was asked.
    [Fact]
    public void A_refused_duplicate_registration_raises_nothing_further()
    {
        var publisher = new RecordingPublisher();
        var registry = new AskUserResponseRegistry(publisher);
        registry.Register(Conversation(), timeout: null);
        registry.AnnounceWaiting(Conversation(), prompt: null);

        Assert.Throws<InvalidOperationException>(() => registry.Register(Conversation(), timeout: null));

        Assert.Single(publisher.Published);
    }

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

    private sealed class ThrowingPublisher : INotificationPublisher
    {
        public Task PublishAsync(Notification notification, CancellationToken ct = default) =>
            throw new InvalidOperationException("notification store is unavailable");
    }
}
