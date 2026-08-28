using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API over the notification store.
/// </summary>
/// <remarks>
/// The single surface every client reads: the portal today, and a phone or desktop app later. Read
/// state is server-side, so marking something read here marks it read everywhere - which is the
/// whole reason the state does not live in the browser.
/// </remarks>
[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController(INotificationStore store) : ControllerBase
{
    private const int MaxLimit = 500;

    private readonly INotificationStore _store = store;

    /// <summary>Lists notifications, newest first.</summary>
    /// <param name="includeRead">Include notifications already read. Defaults to true.</param>
    /// <param name="limit">Maximum to return; capped so a client cannot ask for the whole history.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<NotificationResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NotificationResponse>>> List(
        [FromQuery] bool includeRead = true,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var notifications = await _store.ListAsync(includeRead, Math.Clamp(limit, 1, MaxLimit), ct);

        return Ok(notifications.Select(NotificationResponse.From).ToArray());
    }

    /// <summary>
    /// Number of unread notifications.
    /// </summary>
    /// <remarks>
    /// Separate from the list so a badge does not have to fetch content it will not render. A
    /// client polling for a count should not pay for a hundred rows to learn one number.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadCountResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UnreadCountResponse>> UnreadCount(CancellationToken ct = default) =>
        Ok(new UnreadCountResponse(await _store.UnreadCountAsync(ct)));

    /// <summary>Marks one notification read.</summary>
    /// <param name="id">Notification identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new { error = "A notification id is required." });

        // A 404 here means "no unread notification with that id" - either unknown, or already read.
        // Both are states in which the caller's intent is already satisfied, so neither is an error
        // worth surfacing beyond the status code.
        return await _store.MarkReadAsync(id, ct)
            ? NoContent()
            : NotFound(new { error = $"No unread notification '{id}'." });
    }

    /// <summary>Marks every unread notification read.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("read-all")]
    [ProducesResponseType(typeof(MarkAllReadResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MarkAllReadResponse>> MarkAllRead(CancellationToken ct = default) =>
        Ok(new MarkAllReadResponse(await _store.MarkAllReadAsync(ct)));

    /// <summary>Deletes one notification.</summary>
    /// <param name="id">Notification identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new { error = "A notification id is required." });

        return await _store.DeleteAsync(id, ct)
            ? NoContent()
            : NotFound(new { error = $"Notification '{id}' was not found." });
    }
}

/// <summary>One notification as a client reads it.</summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Kind">What the notification is about.</param>
/// <param name="Severity">How much attention it wants.</param>
/// <param name="Title">One-line summary.</param>
/// <param name="Body">Optional detail.</param>
/// <param name="AgentId">Agent concerned, when any.</param>
/// <param name="ConversationId">Conversation concerned, when any.</param>
/// <param name="Link">Where to go to act on it, when there is anywhere.</param>
/// <param name="CreatedAtUtc">When it was raised.</param>
/// <param name="ReadAtUtc">When it was read, or null while unread.</param>
public sealed record NotificationResponse(
    string Id,
    string Kind,
    string Severity,
    string Title,
    string? Body,
    string? AgentId,
    string? ConversationId,
    string? Link,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadAtUtc)
{
    /// <summary>
    /// Projects a stored notification. Kind and severity are emitted as NAMES rather than the
    /// numbers they are stored as: a mobile client written against this API should not have to
    /// track which integer means "waiting for input", and a renumbering must not silently change
    /// what an existing client displays.
    /// </summary>
    /// <param name="notification">The stored notification.</param>
    public static NotificationResponse From(Notification notification) => new(
        notification.Id,
        notification.Kind.ToString(),
        notification.Severity.ToString(),
        notification.Title,
        notification.Body,
        notification.AgentId,
        notification.ConversationId,
        notification.Link,
        notification.CreatedAtUtc,
        notification.ReadAtUtc);
}

/// <summary>Unread badge count.</summary>
/// <param name="Count">Number of unread notifications.</param>
public sealed record UnreadCountResponse(int Count);

/// <summary>Outcome of marking everything read.</summary>
/// <param name="Changed">How many notifications changed from unread to read.</param>
public sealed record MarkAllReadResponse(int Changed);
