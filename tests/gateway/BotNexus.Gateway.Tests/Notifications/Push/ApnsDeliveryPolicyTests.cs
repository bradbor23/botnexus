using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Notifications.Push;

namespace BotNexus.Gateway.Tests.Notifications.Push;

/// <summary>
/// Pins which notifications reach an iOS device at each notification level (#168).
/// </summary>
/// <remarks>
/// A level is a person's answer to "how much should my phone tell me", and the rules are worth
/// pinning in one place. A phone is woken for what needs the person - an agent waiting for an answer,
/// a run that failed - and, when asked, for finished replies. Scheduled-job outcomes and gateway
/// health are listed in the bell and never pushed: a job failing every five minutes filled a lock
/// screen with things nobody could act on from a phone. The test notification is the exception, so
/// delivery can still be checked. A conversation can be turned up or down on its own, and Off means off.
/// </remarks>
public sealed class ApnsDeliveryPolicyTests
{
    private static ApnsDevice Device(
        ApnsNotificationLevel level,
        params (string ConversationId, ApnsConversationLevel Level)[] conversations) => new()
    {
        DeviceToken = "a1",
        Environment = ApnsEnvironment.Production,
        Level = level,
        ConversationLevels = conversations.ToDictionary(
            c => c.ConversationId, c => c.Level, StringComparer.Ordinal),
    };

    private static Notification Of(NotificationKind kind, string? conversationId = "c1") => new()
    {
        Id = "n1",
        Kind = kind,
        Severity = NotificationSeverity.Info,
        Title = "t",
        ConversationId = conversationId,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    [Theory]
    [InlineData(NotificationKind.AgentWaitingForInput)]
    [InlineData(NotificationKind.AgentRunFailed)]
    public void The_default_level_pushes_what_needs_the_person(NotificationKind kind) =>
        Assert.True(ApnsDeliveryPolicy.ShouldDeliver(Device(ApnsNotificationLevel.NeedsMe), Of(kind)));

    [Theory]
    [InlineData(NotificationKind.CronRunOutcome, ApnsNotificationLevel.NeedsMe)]
    [InlineData(NotificationKind.CronRunOutcome, ApnsNotificationLevel.NeedsMeAndReplies)]
    [InlineData(NotificationKind.GatewayHealth, ApnsNotificationLevel.NeedsMe)]
    [InlineData(NotificationKind.GatewayHealth, ApnsNotificationLevel.NeedsMeAndReplies)]
    public void Scheduled_jobs_and_gateway_health_stay_in_the_bell_at_every_level(
        NotificationKind kind, ApnsNotificationLevel level) =>
        Assert.False(ApnsDeliveryPolicy.ShouldDeliver(Device(level), Of(kind, conversationId: null)));

    // Turning a conversation all the way up is about that conversation's replies, not a way back
    // to being pushed scheduled-job outcomes that happen to name it.
    [Fact]
    public void A_conversation_set_to_all_replies_does_not_push_a_scheduled_job_outcome() =>
        Assert.False(ApnsDeliveryPolicy.ShouldDeliver(
            Device(ApnsNotificationLevel.NeedsMe, ("c1", ApnsConversationLevel.AllReplies)),
            Of(NotificationKind.CronRunOutcome, "c1")));

    // Otherwise the one route that exists to prove a phone can be reached would prove it cannot.
    [Fact]
    public void The_test_notification_is_pushed_so_delivery_can_be_checked()
    {
        var test = Of(NotificationKind.GatewayHealth, conversationId: null) with
        {
            Title = ApnsDeliveryPolicy.TestNotificationTitle,
        };

        Assert.True(ApnsDeliveryPolicy.ShouldDeliver(Device(ApnsNotificationLevel.NeedsMe), test));
        Assert.False(ApnsDeliveryPolicy.ShouldDeliver(Device(ApnsNotificationLevel.Off), test));
    }

    [Fact]
    public void The_default_level_holds_back_a_finished_reply() =>
        Assert.False(ApnsDeliveryPolicy.ShouldDeliver(
            Device(ApnsNotificationLevel.NeedsMe), Of(NotificationKind.AgentRunCompleted)));

    [Fact]
    public void Asking_for_replies_delivers_a_finished_reply() =>
        Assert.True(ApnsDeliveryPolicy.ShouldDeliver(
            Device(ApnsNotificationLevel.NeedsMeAndReplies), Of(NotificationKind.AgentRunCompleted)));

    [Theory]
    [InlineData(NotificationKind.AgentWaitingForInput)]
    [InlineData(NotificationKind.AgentRunCompleted)]
    [InlineData(NotificationKind.GatewayHealth)]
    public void Off_delivers_nothing(NotificationKind kind) =>
        Assert.False(ApnsDeliveryPolicy.ShouldDeliver(Device(ApnsNotificationLevel.Off), Of(kind)));

    [Fact]
    public void A_muted_conversation_is_silent_even_when_an_agent_is_waiting_in_it()
    {
        var device = Device(ApnsNotificationLevel.NeedsMeAndReplies, ("c1", ApnsConversationLevel.Mute));

        Assert.False(ApnsDeliveryPolicy.ShouldDeliver(device, Of(NotificationKind.AgentWaitingForInput, "c1")));
        // Only that conversation.
        Assert.True(ApnsDeliveryPolicy.ShouldDeliver(device, Of(NotificationKind.AgentWaitingForInput, "c2")));
    }

    [Fact]
    public void A_conversation_set_to_all_replies_delivers_its_replies_at_the_default_level()
    {
        var device = Device(ApnsNotificationLevel.NeedsMe, ("c1", ApnsConversationLevel.AllReplies));

        Assert.True(ApnsDeliveryPolicy.ShouldDeliver(device, Of(NotificationKind.AgentRunCompleted, "c1")));
        Assert.False(ApnsDeliveryPolicy.ShouldDeliver(device, Of(NotificationKind.AgentRunCompleted, "c2")));
    }

    [Fact]
    public void A_conversation_set_to_needs_me_holds_back_its_replies_when_the_device_wants_them()
    {
        var device = Device(ApnsNotificationLevel.NeedsMeAndReplies, ("c1", ApnsConversationLevel.NeedsMe));

        Assert.False(ApnsDeliveryPolicy.ShouldDeliver(device, Of(NotificationKind.AgentRunCompleted, "c1")));
        Assert.True(ApnsDeliveryPolicy.ShouldDeliver(device, Of(NotificationKind.AgentWaitingForInput, "c1")));
    }

    // Off is the person saying "nothing on this phone"; a conversation left turned up from before
    // must not quietly override that.
    [Fact]
    public void Off_is_not_overridden_by_a_conversation() =>
        Assert.False(ApnsDeliveryPolicy.ShouldDeliver(
            Device(ApnsNotificationLevel.Off, ("c1", ApnsConversationLevel.AllReplies)),
            Of(NotificationKind.AgentRunCompleted, "c1")));

    [Fact]
    public void A_notification_about_no_conversation_follows_the_device_level() =>
        Assert.True(ApnsDeliveryPolicy.ShouldDeliver(
            Device(ApnsNotificationLevel.NeedsMe, ("c1", ApnsConversationLevel.Mute)),
            Of(NotificationKind.AgentRunFailed, conversationId: null)));
}
