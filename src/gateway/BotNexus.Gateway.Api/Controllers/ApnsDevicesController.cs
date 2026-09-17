using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Notifications;
using BotNexus.Gateway.Notifications.Push;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Lets a native iOS app register the device token APNs gave it.
/// </summary>
/// <remarks>
/// The native counterpart to the web push subscribe endpoints. An app calls register on every
/// launch - iOS re-issues tokens without warning, and re-registering an unchanged one is expected
/// and cheap.
/// </remarks>
[ApiController]
[Route("api/notifications/apns")]
public sealed class ApnsDevicesController(
    IApnsDeviceStore store,
    ApnsOptions options,
    INotificationStore? notifications = null) : ControllerBase
{
    /// <summary>APNs device tokens are 32 bytes, hex-encoded. Newer tokens may be longer.</summary>
    private const int MinTokenLength = 64;
    private const int MaxTokenLength = 200;

    /// <summary>A conversation id is an opaque gateway identifier; anything longer is not one.</summary>
    private const int MaxConversationIdLength = 256;

    private readonly IApnsDeviceStore _store = store;
    private readonly ApnsOptions _options = options;
    private readonly INotificationStore? _notifications = notifications;

    /// <summary>
    /// Whether this gateway can push to iOS at all, so an app can say so rather than registering
    /// into a void and waiting for notifications that will never come.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ApnsStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<ApnsStatusResponse> Status() =>
        Ok(new ApnsStatusResponse
        {
            Configured = _options.IsConfigured,
            BundleId = _options.IsConfigured ? _options.BundleId : null,
        });

    /// <summary>Registers a device token, or refreshes one already held.</summary>
    /// <param name="request">The token and the environment that minted it.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(
        [FromBody] ApnsRegisterRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DeviceToken))
            return BadRequest(new { error = "deviceToken is required." });

        var token = request.DeviceToken.Trim();

        // Checked on the way in rather than at send time. A malformed token is accepted by APNs
        // with a 400 on every future notification, and the app that sent it would go on believing
        // it was registered.
        if (token.Length is < MinTokenLength or > MaxTokenLength || !IsHex(token))
            return BadRequest(new { error = "deviceToken must be a hex-encoded APNs device token." });

        // The environment decides which Apple host the token is valid against, and the two are not
        // interchangeable - a sandbox token sent to production is refused as BadDeviceToken, which
        // reads like a bad token rather than the wrong address. Guessing it would be worse than
        // refusing.
        var environment = ApnsEnvironment.Normalise(request.Environment);

        if (environment is null)
            return BadRequest(new { error = "environment must be 'sandbox' or 'production'." });

        await _store.SaveAsync(
            new ApnsDevice
            {
                DeviceToken = token,
                Environment = environment,
                DeviceName = string.IsNullOrWhiteSpace(request.DeviceName)
                    ? null
                    : request.DeviceName.Trim()[..Math.Min(request.DeviceName.Trim().Length, 128)],
            },
            ct);

        return NoContent();
    }

    /// <summary>Forgets a device. Idempotent.</summary>
    /// <param name="request">The token to forget.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("unregister")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unregister(
        [FromBody] ApnsUnregisterRequest request,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request?.DeviceToken))
            await _store.RemoveAsync(request.DeviceToken.Trim(), ct);

        return NoContent();
    }

    /// <summary>Every registered device and its notification level (#168).</summary>
    /// <remarks>
    /// Until this existed there was no way to see which phones a gateway pushes to at all: a
    /// registration that never arrived looked exactly like one that did.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("devices")]
    [ProducesResponseType(typeof(IReadOnlyList<ApnsDeviceResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ApnsDeviceResponse>>> Devices(CancellationToken ct = default)
    {
        var devices = await _store.ListAsync(ct);

        return Ok(devices.Select(ApnsDeviceResponse.From).ToList());
    }

    /// <summary>One device's notification level and its conversation levels (#168).</summary>
    /// <param name="deviceToken">The device token the app registered.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("devices/{deviceToken}/preferences")]
    [ProducesResponseType(typeof(ApnsPreferencesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApnsPreferencesResponse>> Preferences(string deviceToken, CancellationToken ct = default)
    {
        var device = await _store.GetAsync(deviceToken.Trim(), ct);

        return device is null ? NotFound() : Ok(ApnsPreferencesResponse.From(device));
    }

    /// <summary>The number a push to this device would put on the app icon (#168).</summary>
    /// <remarks>
    /// The app sets its own icon when it opens and when something is read, and has to land on the
    /// same number a push carries or the icon flickers between two answers. Not found when the
    /// device is unknown or the gateway has nothing to count from - a zero would clear a badge the
    /// phone was rightly showing.
    /// </remarks>
    /// <param name="deviceToken">The device token the app registered.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("devices/{deviceToken}/badge")]
    [ProducesResponseType(typeof(ApnsBadgeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApnsBadgeResponse>> Badge(string deviceToken, CancellationToken ct = default)
    {
        if (_notifications is null)
            return NotFound();

        var device = await _store.GetAsync(deviceToken.Trim(), ct);

        if (device is null)
            return NotFound();

        var unread = await ApnsBadge.ListUnreadAsync(_notifications, ct);

        return Ok(new ApnsBadgeResponse { Count = ApnsBadge.Count(device, unread) });
    }

    /// <summary>Sets a device's notification level (#168).</summary>
    /// <param name="deviceToken">The device token the app registered.</param>
    /// <param name="request"><c>needsMe</c>, <c>needsMeAndReplies</c> or <c>off</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("devices/{deviceToken}/preferences")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetPreferences(
        string deviceToken,
        [FromBody] ApnsLevelRequest request,
        CancellationToken ct = default)
    {
        if (!ApnsLevelNames.TryParseDevice(request?.Level, out var level))
            return BadRequest(new { error = "level must be 'needsMe', 'needsMeAndReplies' or 'off'." });

        return await _store.SetLevelAsync(deviceToken.Trim(), level, ct) ? NoContent() : NotFound();
    }

    /// <summary>Sets one conversation's level on a device (#168).</summary>
    /// <param name="deviceToken">The device token the app registered.</param>
    /// <param name="conversationId">The conversation to turn up or down.</param>
    /// <param name="request"><c>mute</c>, <c>needsMe</c> or <c>allReplies</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("devices/{deviceToken}/conversations/{conversationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetConversationLevel(
        string deviceToken,
        string conversationId,
        [FromBody] ApnsLevelRequest request,
        CancellationToken ct = default)
    {
        if (!ApnsLevelNames.TryParseConversation(request?.Level, out var level))
            return BadRequest(new { error = "level must be 'mute', 'needsMe' or 'allReplies'." });

        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > MaxConversationIdLength)
            return BadRequest(new { error = "conversationId is required." });

        return await _store.SetConversationLevelAsync(deviceToken.Trim(), ConversationId.From(conversationId), level, ct)
            ? NoContent()
            : NotFound();
    }

    /// <summary>Clears one conversation's level on a device, so the device level applies again (#168).</summary>
    /// <param name="deviceToken">The device token the app registered.</param>
    /// <param name="conversationId">The conversation whose level to clear.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("devices/{deviceToken}/conversations/{conversationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearConversationLevel(
        string deviceToken,
        string conversationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > MaxConversationIdLength)
            return BadRequest(new { error = "conversationId is required." });

        return await _store.SetConversationLevelAsync(deviceToken.Trim(), ConversationId.From(conversationId), level: null, ct)
            ? NoContent()
            : NotFound();
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }
}

/// <summary>Whether the gateway is set up to push to iOS.</summary>
public sealed class ApnsStatusResponse
{
    /// <summary>False when gateway:apns is incomplete; registering would achieve nothing.</summary>
    [JsonPropertyName("configured")] public required bool Configured { get; init; }

    /// <summary>The bundle id pushes are sent for, so an app can check it matches its own.</summary>
    [JsonPropertyName("bundleId")] public string? BundleId { get; init; }
}

/// <summary>The number on a device's app icon (#168).</summary>
public sealed class ApnsBadgeResponse
{
    /// <summary>Unread notifications of the kinds this device is pushed.</summary>
    [JsonPropertyName("count")] public required int Count { get; init; }
}

/// <summary>A device token as iOS handed it to the app.</summary>
public sealed class ApnsRegisterRequest
{
    /// <summary>Hex-encoded APNs device token.</summary>
    [JsonPropertyName("deviceToken")] public string? DeviceToken { get; init; }

    /// <summary>Either <c>sandbox</c> or <c>production</c>, matching the build.</summary>
    [JsonPropertyName("environment")] public string? Environment { get; init; }

    /// <summary>Optional label for diagnosis, such as the device name.</summary>
    [JsonPropertyName("deviceName")] public string? DeviceName { get; init; }
}

/// <summary>One registered device, as listed (#168).</summary>
public sealed class ApnsDeviceResponse
{
    /// <summary>Hex-encoded APNs device token.</summary>
    [JsonPropertyName("deviceToken")] public required string DeviceToken { get; init; }

    /// <summary><c>sandbox</c> or <c>production</c>.</summary>
    [JsonPropertyName("environment")] public required string Environment { get; init; }

    /// <summary>The label the app registered with, if any.</summary>
    [JsonPropertyName("deviceName")] public string? DeviceName { get; init; }

    /// <summary><c>needsMe</c>, <c>needsMeAndReplies</c> or <c>off</c>.</summary>
    [JsonPropertyName("level")] public required string Level { get; init; }

    /// <summary>When it first registered.</summary>
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When APNs last accepted a push for it, or null if never.</summary>
    [JsonPropertyName("lastSuccessAtUtc")] public DateTimeOffset? LastSuccessAtUtc { get; init; }

    internal static ApnsDeviceResponse From(ApnsDevice device) => new()
    {
        DeviceToken = device.DeviceToken,
        Environment = device.Environment,
        DeviceName = device.DeviceName,
        Level = ApnsLevelNames.ToWire(device.Level),
        CreatedAtUtc = device.CreatedAtUtc,
        LastSuccessAtUtc = device.LastSuccessAtUtc,
    };
}

/// <summary>A device's notification level and its conversation levels (#168).</summary>
public sealed class ApnsPreferencesResponse
{
    /// <summary><c>needsMe</c>, <c>needsMeAndReplies</c> or <c>off</c>.</summary>
    [JsonPropertyName("level")] public required string Level { get; init; }

    /// <summary>Conversations turned up or down on this device.</summary>
    [JsonPropertyName("conversations")] public required IReadOnlyList<ApnsConversationLevelResponse> Conversations { get; init; }

    internal static ApnsPreferencesResponse From(ApnsDevice device) => new()
    {
        Level = ApnsLevelNames.ToWire(device.Level),
        Conversations = device.ConversationLevels
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ApnsConversationLevelResponse
            {
                ConversationId = pair.Key,
                Level = ApnsLevelNames.ToWire(pair.Value),
            })
            .ToList(),
    };
}

/// <summary>One conversation's level on a device (#168).</summary>
public sealed class ApnsConversationLevelResponse
{
    /// <summary>The conversation.</summary>
    [JsonPropertyName("conversationId")] public required string ConversationId { get; init; }

    /// <summary><c>mute</c>, <c>needsMe</c> or <c>allReplies</c>.</summary>
    [JsonPropertyName("level")] public required string Level { get; init; }
}

/// <summary>A level to set, by its wire name (#168).</summary>
public sealed class ApnsLevelRequest
{
    /// <summary>The level's wire name.</summary>
    [JsonPropertyName("level")] public string? Level { get; init; }
}

/// <summary>The device token to forget.</summary>
public sealed class ApnsUnregisterRequest
{
    /// <summary>Hex-encoded APNs device token.</summary>
    [JsonPropertyName("deviceToken")] public string? DeviceToken { get; init; }
}
