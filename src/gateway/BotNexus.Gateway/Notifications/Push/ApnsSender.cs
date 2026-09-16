using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// Delivers a notification to registered iOS devices through Apple Push Notification service.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="WebPushSender"/> for a native iOS app, which cannot use web push:
/// Apple only wakes a native app for a push that came from APNs. Same broadcaster, same
/// best-effort contract - the notification is in the store before any of this runs, so a failed
/// push costs immediacy and nothing else.
/// </remarks>
public sealed class ApnsSender(
    HttpClient http,
    IApnsDeviceStore store,
    ApnsOptions options,
    ApnsTokenProvider tokens,
    IWaitingConversationCount? waiting = null,
    IPendingQuestionLookup? questions = null,
    ILogger<ApnsSender>? logger = null)
{
    /// <summary>APNs rejects an alert payload larger than this.</summary>
    private const int MaxPayloadBytes = 4096;

    /// <summary>
    /// Absent keys rather than null ones. A notification with no question would otherwise carry four
    /// nulls, and a payload has 4 KB to say everything in - but the deciding reason is that a client
    /// asking "is there a question here" should get an answer from whether the key exists, not from
    /// having to tell a null apart from a value.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>How long APNs should hold a push for a device that is offline.</summary>
    private static readonly TimeSpan Expiration = TimeSpan.FromHours(24);

    private readonly HttpClient _http = http;
    private readonly IApnsDeviceStore _store = store;
    private readonly ApnsOptions _options = options;
    private readonly ApnsTokenProvider _tokens = tokens;

    /// <summary>
    /// Counts what is waiting on the person, for the number on the app icon. Absent on a gateway
    /// wired without one, and then no badge is sent at all rather than a guessed zero - which would
    /// clear a count the phone was rightly showing.
    /// </summary>
    private readonly IWaitingConversationCount? _waiting = waiting;

    /// <summary>
    /// Finds the question a conversation is waiting on, so a notification can carry its answers.
    /// Absent on a gateway wired without it, and a question then arrives as words alone.
    /// </summary>
    private readonly IPendingQuestionLookup? _questions = questions;
    private readonly ILogger<ApnsSender> _logger = logger ?? NullLogger<ApnsSender>.Instance;

    /// <summary>Pushes one notification to every registered device. Returns how many were accepted.</summary>
    public async Task<int> SendAsync(Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // An operator with no Apple Developer account is the normal case, not a misconfiguration.
        // Nothing is attempted and nothing is logged.
        if (!_options.IsConfigured)
            return 0;

        var devices = await _store.ListAsync(ct).ConfigureAwait(false);

        if (devices.Count == 0)
            return 0;

        // Counted once per notification rather than per device: the number is the same for every
        // phone, and it is a database read.
        var badge = _waiting is null
            ? null
            : await CountWaitingAsync(ct).ConfigureAwait(false);

        // Only a question has answers, and only a question pays for the read that finds them.
        var question = notification.Kind == NotificationKind.AgentWaitingForInput
            ? await FindQuestionAsync(notification, ct).ConfigureAwait(false)
            : null;

        var payload = BuildPayload(notification, badge, question);
        var delivered = 0;

        foreach (var device in devices)
        {
            // #168: what a device is sent is that device's own choice, decided per device - one phone
            // asking for every finished reply must not wake another that did not.
            if (!ApnsDeliveryPolicy.ShouldDeliver(device, notification))
                continue;

            if (await SendOneAsync(device, notification, payload, ct).ConfigureAwait(false))
                delivered++;
        }

        return delivered;
    }

    /// <summary>
    /// Builds the aps payload, trimming the body if the whole thing would exceed Apple's limit.
    /// </summary>
    /// <remarks>
    /// An oversize payload is rejected outright with PayloadTooLarge - the notification is not
    /// truncated for you, it simply does not arrive. A long provider error in the body is a
    /// realistic way to hit 4KB, so it is trimmed here rather than lost there.
    /// </remarks>
    /// <summary>The waiting count, or nothing when it cannot be read.</summary>
    /// <remarks>
    /// A failure to count must not cost the notification: the push is what the person is waiting
    /// for, and a missing badge leaves the icon as it was rather than lying about it.
    /// </remarks>
    private async Task<int?> CountWaitingAsync(CancellationToken ct)
    {
        try
        {
            return await _waiting!.CountAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not count waiting conversations; sending the push without a badge.");

            return null;
        }
    }

    /// <summary>
    /// How many answers a phone can show as buttons. Beyond this it shows none: three of nine
    /// buttons hides the one someone wanted, and opening the conversation shows them all.
    /// </summary>
    /// <remarks>
    /// The client registers categories for exactly these shapes, so this number is part of the
    /// contract rather than a local preference - changing it here alone would name a category the
    /// app never registered, and the buttons would silently stop appearing.
    /// </remarks>
    internal const int MaxChoiceButtons = 3;

    /// <summary>The category naming the buttons a question is shown with.</summary>
    internal static string CategoryFor(PendingQuestion question)
    {
        var buttons = question.Choices.Count is > 0 and <= MaxChoiceButtons ? question.Choices.Count : 0;

        return $"botnexus.question.{buttons}{(question.AllowFreeForm ? ".reply" : string.Empty)}";
    }

    /// <summary>The question this notification is about, or nothing when it cannot be read.</summary>
    /// <remarks>
    /// Losing the answers costs the notification its buttons. Losing the notification would cost the
    /// person the thing they are waiting for, so every failure here is swallowed.
    /// </remarks>
    private async Task<PendingQuestion?> FindQuestionAsync(Notification notification, CancellationToken ct)
    {
        if (_questions is null || string.IsNullOrEmpty(notification.ConversationId))
            return null;

        try
        {
            return await _questions
                .FindAsync(ConversationId.From(notification.ConversationId), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not read the pending question for conversation '{ConversationId}'; sending the notification without its answers.",
                notification.ConversationId);

            return null;
        }
    }

    internal static byte[] BuildPayload(
        Notification notification,
        int? badge = null,
        PendingQuestion? question = null)
    {
        var body = notification.Body;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            // A dictionary rather than an anonymous type, because "thread-id" is not a valid C# name.
            var aps = new Dictionary<string, object?>
            {
                ["alert"] = new { title = notification.Title, body },
                ["sound"] = "default",
                // For anything but a question this routes a tap. For a question it does more: iOS
                // attaches buttons by matching this against a category the app registered BEFORE
                // the notification arrived, so it names the question's shape rather than its kind.
                // The naming is a contract with the client, written down in
                // docs/development/notification-clients.md.
                ["category"] = question is null
                    ? notification.Kind.ToString()
                    : CategoryFor(question),
            };

            // #168: groups one conversation's notifications together on the lock screen instead of
            // interleaving them with every other conversation's.
            if (!string.IsNullOrEmpty(notification.ConversationId))
                aps["thread-id"] = notification.ConversationId;

            // #168: the count on the app icon. Zero is meaningful and must be sent - it is what
            // takes the badge off once the last question has been answered.
            if (badge is { } waitingCount)
                aps["badge"] = waitingCount;

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                aps,
                id = notification.Id,
                kind = notification.Kind.ToString(),
                severity = notification.Severity.ToString(),
                link = notification.Link,
                // #168: so an app can open the conversation without first asking which agent owns it.
                agentId = notification.AgentId,
                conversationId = notification.ConversationId,
                // #168: what the question offers, so a phone can answer it the way Telegram does
                // rather than only announcing that a question exists.
                requestId = question?.RequestId,
                choices = question is null
                    ? null
                    : question.Choices.Select(choice => new { value = choice.Value, label = choice.Label }).ToArray(),
                allowFreeForm = question?.AllowFreeForm,
                allowMultiple = question?.AllowMultiple,
            },
            PayloadOptions);

            if (bytes.Length <= MaxPayloadBytes || string.IsNullOrEmpty(body))
                return bytes;

            // Halve the body and try again. The title is never trimmed: it is the part written to
            // be read on a lock screen, and a notification with no title is useless.
            var keep = Math.Max(0, body.Length / 2);
            body = keep == 0 ? null : body[..keep];
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                aps = new { alert = new { title = notification.Title } },
                id = notification.Id,
            },
            PayloadOptions);
    }

    private async Task<bool> SendOneAsync(
        ApnsDevice device,
        Notification notification,
        byte[] payload,
        CancellationToken ct)
    {
        try
        {
            var url = $"{ApnsEnvironment.HostFor(device.Environment)}/3/device/{device.DeviceToken}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                // APNs speaks HTTP/2 only, and refuses the connection rather than negotiating down.
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(payload),
            };

            request.Headers.TryAddWithoutValidation("authorization", $"bearer {_tokens.GetToken()}");
            request.Headers.TryAddWithoutValidation("apns-topic", _options.BundleId);
            request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            request.Headers.TryAddWithoutValidation("apns-priority", "10");
            request.Headers.TryAddWithoutValidation(
                "apns-expiration",
                DateTimeOffset.UtcNow.Add(Expiration).ToUnixTimeSeconds().ToString());

            // Same notification twice replaces the earlier alert rather than stacking a duplicate,
            // matching what the web push tag does.
            if (!string.IsNullOrEmpty(notification.Id))
                request.Headers.TryAddWithoutValidation("apns-collapse-id", Collapse(notification.Id));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                await _store.MarkDeliveredAsync(device.DeviceToken, ct).ConfigureAwait(false);
                return true;
            }

            await HandleRefusalAsync(device, response, ct).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push a notification to an iOS device.");
            return false;
        }
    }

    private async Task HandleRefusalAsync(
        ApnsDevice device,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var reason = await ReadReasonAsync(response, ct).ConfigureAwait(false);

        // 410 is Apple saying the app is gone from this device. BadDeviceToken means the token was
        // never valid here - most often a sandbox token sent to production, or the reverse. Both
        // are permanent for this row, and keeping it would retry forever in silence.
        var permanent = response.StatusCode == HttpStatusCode.Gone
            || string.Equals(reason, "BadDeviceToken", StringComparison.Ordinal)
            || string.Equals(reason, "Unregistered", StringComparison.Ordinal);

        if (permanent)
        {
            await _store.RemoveAsync(device.DeviceToken, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Removed an iOS device registration; APNs reported {Status} {Reason}.",
                (int)response.StatusCode,
                reason ?? "(no reason)");

            return;
        }

        // Everything else is the gateway's problem or Apple's, not the device's. A configuration
        // fault is worth saying loudly, because it silently affects EVERY device.
        if (reason is "InvalidProviderToken" or "ExpiredProviderToken" or "TopicDisallowed" or "MissingTopic")
        {
            _logger.LogError(
                "APNs rejected the gateway's credentials with {Reason}. No iOS device will receive "
                + "notifications until gateway:apns is corrected.",
                reason);

            return;
        }

        _logger.LogWarning(
            "APNs refused a notification with {Status} {Reason}.",
            (int)response.StatusCode,
            reason ?? "(no reason)");
    }

    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
                return null;

            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("reason", out var reason)
                ? reason.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            // A refusal we cannot parse is still a refusal; the status code carries enough.
            return null;
        }
    }

    /// <summary>APNs caps the collapse identifier at 64 bytes.</summary>
    private static string Collapse(string id) =>
        Encoding.UTF8.GetByteCount(id) <= 64 ? id : id[..64];
}
