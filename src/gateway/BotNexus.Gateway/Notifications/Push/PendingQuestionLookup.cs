using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>One answer a person can tap, as a notification carries it (#168).</summary>
/// <param name="Value">What the agent receives when this answer is chosen.</param>
/// <param name="Label">What the person reads.</param>
public readonly record struct PendingQuestionChoice(string Value, string Label);

/// <summary>The question a conversation is waiting on, in the shape a push needs (#168).</summary>
/// <param name="RequestId">Ties an answer back to the question that was asked.</param>
/// <param name="Choices">The answers to offer as buttons, empty when the question is open-ended.</param>
/// <param name="AllowFreeForm">Whether an answer may be typed rather than chosen.</param>
/// <param name="AllowMultiple">Whether more than one choice may be picked.</param>
public sealed record PendingQuestion(
    string RequestId,
    IReadOnlyList<PendingQuestionChoice> Choices,
    bool AllowFreeForm,
    bool AllowMultiple);

/// <summary>Finds the question a conversation is currently waiting on.</summary>
public interface IPendingQuestionLookup
{
    /// <summary>The pending question, or <see langword="null"/> when nothing is waiting.</summary>
    Task<PendingQuestion?> FindAsync(ConversationId conversationId, CancellationToken ct = default);
}

/// <summary>Reads the pending question from the conversation row that already holds it.</summary>
/// <remarks>
/// The prompt is persisted as JSON on the conversation so a reloaded tab can rehydrate it (#1488).
/// Reading the same field at push time is what lets a notification carry the answers without the
/// notification contract growing fields it would have to persist, and without a store migration for
/// something the store already keeps.
/// <para>
/// Every failure returns nothing. A malformed or newer payload costs the notification its buttons,
/// which still leaves a notification that names the question and opens the conversation — the thing
/// it must never cost is the notification itself.
/// </para>
/// </remarks>
public sealed class PendingQuestionLookup(
    IConversationStore conversations,
    ILogger<PendingQuestionLookup>? logger = null) : IPendingQuestionLookup
{
    /// <summary>
    /// The same casing the checkpoint services read this field with. A fourth reader that disagreed
    /// would parse nothing and look, from the outside, exactly like a conversation with no question.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConversationStore _conversations = conversations;
    private readonly ILogger<PendingQuestionLookup> _logger = logger ?? NullLogger<PendingQuestionLookup>.Instance;

    /// <inheritdoc />
    public async Task<PendingQuestion?> FindAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        var conversation = await _conversations.GetAsync(conversationId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(conversation?.PendingAskUserJson))
            return null;

        AskUserRequest? pending;
        try
        {
            pending = JsonSerializer.Deserialize<AskUserRequest>(conversation.PendingAskUserJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "The pending question on conversation '{ConversationId}' could not be read; its notification will carry no answers.",
                conversationId.Value);

            return null;
        }

        if (pending is null || string.IsNullOrWhiteSpace(pending.RequestId))
            return null;

        var choices = pending.Choices is { Count: > 0 }
            ? pending.Choices
                .Select(choice => new PendingQuestionChoice(
                    choice.Value,
                    string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label))
                .ToArray()
            : [];

        return new PendingQuestion(
            pending.RequestId,
            choices,
            pending.AllowFreeForm,
            pending.AllowMultiple);
    }
}
