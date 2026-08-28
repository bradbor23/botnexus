using BotNexus.Gateway.Abstractions.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Notifications;

/// <summary>
/// Writes raised notifications to the store, swallowing failures.
/// </summary>
public sealed class NotificationPublisher(
    INotificationStore store,
    ILogger<NotificationPublisher>? logger = null) : INotificationPublisher
{
    private readonly INotificationStore _store = store;
    private readonly ILogger<NotificationPublisher> _logger = logger ?? NullLogger<NotificationPublisher>.Instance;

    /// <inheritdoc />
    public async Task PublishAsync(Notification notification, CancellationToken ct = default)
    {
        if (notification is null)
            return;

        try
        {
            await _store.AppendAsync(notification, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged, not rethrown: the caller is in the middle of doing the actual work, and a
            // failure to record a note about it is not a reason to fail that work.
            _logger.LogWarning(
                ex,
                "Could not record the {Kind} notification '{Title}'.",
                notification.Kind,
                notification.Title);
        }
    }
}
