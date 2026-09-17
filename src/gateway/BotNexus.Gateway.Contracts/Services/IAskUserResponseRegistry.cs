using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Services;

/// <summary>
/// Coordinates pending <c>ask_user</c> waits outside the session queue so channels can
/// fulfill blocked tool calls directly and resume agent execution safely.
/// </summary>
public interface IAskUserResponseRegistry
{
    /// <summary>
    /// Registers a new pending request for a conversation and returns the request correlation
    /// identifier plus the task that completes when a response arrives.
    /// </summary>
    /// <param name="conversationId">Conversation that owns the pending request.</param>
    /// <param name="timeout">Optional timeout after which the wait completes as timed out.</param>
    /// <returns>Generated request id and completion task.</returns>
    /// <remarks>
    /// Registering raises nothing. The caller announces the question with
    /// <see cref="AnnounceWaiting"/> once it is saved, because a phone's push reads the question's
    /// answers from the saved copy (#168).
    /// </remarks>
    (string RequestId, Task<AskUserResponse> Task) Register(
        ConversationId conversationId,
        TimeSpan? timeout);

    /// <summary>
    /// Tells people a question is waiting on them: raises the <c>AgentWaitingForInput</c>
    /// notification that reaches the bell and, through it, a phone.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Register"/> so it can come after the pending prompt is saved on the
    /// conversation. A push carries the question's answers as buttons by reading that saved copy;
    /// raised any earlier, it raced the save and often went out with no answers to tap (#168).
    /// Never throws: a question must not fail because reporting it did.
    /// </remarks>
    /// <param name="conversationId">Conversation that is waiting.</param>
    /// <param name="prompt">
    /// The question being asked, when the caller has it. It becomes what the notification says, so a
    /// phone shows the question rather than the fact that one exists.
    /// </param>
    void AnnounceWaiting(ConversationId conversationId, string? prompt);

    /// <summary>
    /// Rebuilds the conversation-to-request-id mapping for a durable pending prompt whose live
    /// waiter did not survive a gateway restart, reload, or conversation switch (issue #2047).
    /// The rehydrated entry has no completion task and is intentionally <em>not</em> completable via
    /// <see cref="TryComplete"/> - a response for it must go through the durable checkpoint claim so
    /// the conversation actually resumes from persisted state. Its only role is to make
    /// <see cref="TryGetPendingRequestId"/> report the prompt so ordinary inbound text is still
    /// intercepted as a response rather than mis-dispatched as a fresh turn. Idempotent: a no-op when
    /// a live or rehydrated entry already exists for the conversation.
    /// </summary>
    /// <param name="conversationId">Conversation that owns the durable pending prompt.</param>
    /// <param name="requestId">Correlation id read from the persisted checkpoint.</param>
    /// <returns><c>true</c> when a new rehydrated entry was added; <c>false</c> when one already existed.</returns>
    bool Rehydrate(ConversationId conversationId, string requestId);

    /// <summary>
    /// Attempts to complete a pending request for the specified conversation.
    /// Returns <c>false</c> when no matching pending request exists.
    /// </summary>
    bool TryComplete(ConversationId conversationId, string requestId, AskUserResponse response);

    /// <summary>
    /// Cancels a pending request by request id if it is still waiting.
    /// </summary>
    void Cancel(string requestId);

    /// <summary>
    /// Cancels all pending requests for a conversation, typically during archive/close.
    /// </summary>
    void CancelAllForConversation(ConversationId conversationId);

    /// <summary>
    /// Returns the pending request id for a conversation when a wait is active or a durable
    /// checkpoint has been rehydrated.
    /// </summary>
    bool TryGetPendingRequestId(ConversationId conversationId, out string requestId);
}
