using BotNexus.Gateway.Abstractions.Notifications;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>How much an iOS device wants to be told (#168).</summary>
/// <remarks>
/// A person's answer to "how much should my phone tell me", chosen in the app. The default is what
/// every device received before levels existed, so adding levels changed nothing for a phone that
/// never picked one.
/// </remarks>
public enum ApnsNotificationLevel
{
    /// <summary>
    /// What needs the person: an agent waiting for an answer, and a run that failed. The default.
    /// </summary>
    NeedsMe = 0,

    /// <summary>All of <see cref="NeedsMe"/>, and each time an agent finishes replying.</summary>
    NeedsMeAndReplies = 1,

    /// <summary>Nothing at all on this device.</summary>
    Off = 2,
}

/// <summary>One conversation's level on one device, which overrides the device's own (#168).</summary>
public enum ApnsConversationLevel
{
    /// <summary>Nothing from this conversation, even when an agent in it is waiting.</summary>
    Mute = 0,

    /// <summary>Only what needs the person - never a finished reply.</summary>
    NeedsMe = 1,

    /// <summary>Everything from this conversation, finished replies included.</summary>
    AllReplies = 2,
}

/// <summary>The level names as they travel over the API and are kept in the device store.</summary>
public static class ApnsLevelNames
{
    /// <summary>The wire name of a device level.</summary>
    public static string ToWire(ApnsNotificationLevel level) => level switch
    {
        ApnsNotificationLevel.NeedsMeAndReplies => "needsMeAndReplies",
        ApnsNotificationLevel.Off => "off",
        _ => "needsMe",
    };

    /// <summary>The wire name of a conversation level.</summary>
    public static string ToWire(ApnsConversationLevel level) => level switch
    {
        ApnsConversationLevel.Mute => "mute",
        ApnsConversationLevel.AllReplies => "allReplies",
        _ => "needsMe",
    };

    /// <summary>Reads a device level by its wire name, ignoring case.</summary>
    public static bool TryParseDevice(string? value, out ApnsNotificationLevel level)
    {
        foreach (var candidate in Enum.GetValues<ApnsNotificationLevel>())
        {
            if (string.Equals(ToWire(candidate), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                level = candidate;
                return true;
            }
        }

        level = ApnsNotificationLevel.NeedsMe;
        return false;
    }

    /// <summary>Reads a conversation level by its wire name, ignoring case.</summary>
    public static bool TryParseConversation(string? value, out ApnsConversationLevel level)
    {
        foreach (var candidate in Enum.GetValues<ApnsConversationLevel>())
        {
            if (string.Equals(ToWire(candidate), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                level = candidate;
                return true;
            }
        }

        level = ApnsConversationLevel.NeedsMe;
        return false;
    }
}

/// <summary>Decides whether one notification is pushed to one device (#168).</summary>
public static class ApnsDeliveryPolicy
{
    /// <summary>The title of the notification <c>POST /api/notifications/test</c> raises.</summary>
    /// <remarks>
    /// Shared with the route that raises it so the one notification whose whole purpose is to prove
    /// a phone can be reached is not the one kept off it.
    /// </remarks>
    public const string TestNotificationTitle = "Test notification";

    /// <summary>Whether <paramref name="notification"/> should reach <paramref name="device"/>.</summary>
    /// <remarks>
    /// Off wins over everything, including a conversation left turned up from before: it is the
    /// person saying nothing on this phone. Otherwise a conversation's own level, when it has one,
    /// decides for notifications about that conversation, and the device level decides for the rest.
    /// A finished reply is the one kind a device has to ask for; a waiting agent and a failed run
    /// are what the default is for.
    /// </remarks>
    public static bool ShouldDeliver(ApnsDevice device, Notification notification)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(notification);

        if (device.Level == ApnsNotificationLevel.Off || !IsPushedToPhones(notification))
            return false;

        var isReply = notification.Kind == NotificationKind.AgentRunCompleted;

        if (!string.IsNullOrEmpty(notification.ConversationId)
            && device.ConversationLevels.TryGetValue(notification.ConversationId, out var conversationLevel))
        {
            return conversationLevel switch
            {
                ApnsConversationLevel.Mute => false,
                ApnsConversationLevel.AllReplies => true,
                _ => !isReply,
            };
        }

        return !isReply || device.Level == ApnsNotificationLevel.NeedsMeAndReplies;
    }

    /// <summary>Whether this kind of notification belongs on a lock screen at all.</summary>
    /// <remarks>
    /// A scheduled job's outcome and the gateway's own health are worth listing and worth counting,
    /// and neither is worth waking someone for: a job that fails on every run fills a lock screen
    /// with the same thing all day, and nothing there can be acted on from a phone. Both still reach
    /// every client through the store and SignalR. The test notification is let through because it
    /// exists to answer "does this phone get notifications", which it could not otherwise do.
    /// </remarks>
    private static bool IsPushedToPhones(Notification notification) => notification.Kind switch
    {
        NotificationKind.AgentWaitingForInput
            or NotificationKind.AgentRunFailed
            or NotificationKind.AgentRunCompleted => true,
        NotificationKind.GatewayHealth =>
            string.Equals(notification.Title, TestNotificationTitle, StringComparison.Ordinal),
        _ => false,
    };
}
