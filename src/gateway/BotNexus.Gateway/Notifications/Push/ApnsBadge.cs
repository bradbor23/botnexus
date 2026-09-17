using BotNexus.Gateway.Abstractions.Notifications;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>The number on an iOS app icon (#168).</summary>
/// <remarks>
/// <para>
/// It counts what is unread and would have reached this phone: the notifications
/// <see cref="ApnsDeliveryPolicy"/> lets through to the device, less the ones already read anywhere.
/// That keeps the icon in step with the lock screen - a job failure or gateway notice that never
/// buzzed does not hold a number on the icon either - and reading clears it, on any client, because
/// read state lives in the one store.
/// </para>
/// <para>
/// The count is decided per device, like delivery: one phone asking for every finished reply must
/// not show a number on another that did not.
/// </para>
/// </remarks>
public static class ApnsBadge
{
    /// <summary>How many unread notifications are read to count from.</summary>
    /// <remarks>
    /// A backlog larger than this is a number nobody reads digit by digit, and bounding the read keeps
    /// every push from paying for a table scan of a neglected inbox.
    /// </remarks>
    internal const int UnreadWindow = 500;

    /// <summary>The unread notifications a badge is counted from, newest first.</summary>
    public static Task<IReadOnlyList<Notification>> ListUnreadAsync(
        INotificationStore notifications,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notifications);

        return notifications.ListAsync(includeRead: false, limit: UnreadWindow, ct);
    }

    /// <summary>How many of <paramref name="unread"/> would have been pushed to <paramref name="device"/>.</summary>
    public static int Count(ApnsDevice device, IEnumerable<Notification> unread)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(unread);

        return unread.Count(notification => ApnsDeliveryPolicy.ShouldDeliver(device, notification));
    }
}
