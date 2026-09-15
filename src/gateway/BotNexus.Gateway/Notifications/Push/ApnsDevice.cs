using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>One iOS device registered for notifications.</summary>
public sealed record ApnsDevice
{
    /// <summary>The APNs device token, hex. Identifies the install, not the person.</summary>
    public required string DeviceToken { get; init; }

    /// <summary>Which Apple environment minted it - sandbox or production.</summary>
    public required string Environment { get; init; }

    /// <summary>What registered, for diagnosis. Optional.</summary>
    public string? DeviceName { get; init; }

    /// <summary>When it was first stored.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When APNs last accepted a push for it, or null if never.</summary>
    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    /// <summary>How much this device wants to be told (#168). The default until changed.</summary>
    public ApnsNotificationLevel Level { get; init; } = ApnsNotificationLevel.NeedsMe;

    /// <summary>Conversations turned up or down on this device, by conversation id (#168).</summary>
    public IReadOnlyDictionary<string, ApnsConversationLevel> ConversationLevels { get; init; } =
        new Dictionary<string, ApnsConversationLevel>(StringComparer.Ordinal);
}

/// <summary>Where iOS device tokens are kept.</summary>
public interface IApnsDeviceStore
{
    /// <summary>Creates the schema.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores a device token, replacing any earlier registration of the same token.
    /// </summary>
    /// <remarks>
    /// Upsert, because iOS re-issues a token on its own schedule - after a restore, an update, or
    /// for no visible reason - and an app is expected to re-register on every launch. Rejecting a
    /// token already held would drop notifications for a device that just asked for them.
    /// </remarks>
    Task SaveAsync(ApnsDevice device, CancellationToken ct = default);

    /// <summary>Every registered device.</summary>
    Task<IReadOnlyList<ApnsDevice>> ListAsync(CancellationToken ct = default);

    /// <summary>Removes one by token. Returns whether it existed.</summary>
    Task<bool> RemoveAsync(string deviceToken, CancellationToken ct = default);

    /// <summary>Records that APNs accepted a push.</summary>
    Task MarkDeliveredAsync(string deviceToken, CancellationToken ct = default);

    /// <summary>One device by token, with its levels, or <c>null</c> when it is not registered.</summary>
    Task<ApnsDevice?> GetAsync(string deviceToken, CancellationToken ct = default);

    /// <summary>
    /// Sets a device's notification level. Returns <c>false</c>, and creates nothing, for a token
    /// that is not registered.
    /// </summary>
    Task<bool> SetLevelAsync(string deviceToken, ApnsNotificationLevel level, CancellationToken ct = default);

    /// <summary>
    /// Sets one conversation's level on a device, or clears it when <paramref name="level"/> is
    /// <c>null</c>. Returns <c>false</c> for a token that is not registered.
    /// </summary>
    Task<bool> SetConversationLevelAsync(
        string deviceToken,
        ConversationId conversationId,
        ApnsConversationLevel? level,
        CancellationToken ct = default);
}
