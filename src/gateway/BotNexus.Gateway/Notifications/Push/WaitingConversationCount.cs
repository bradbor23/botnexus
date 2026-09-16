using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>How many conversations are waiting on the person right now (#168).</summary>
/// <remarks>
/// This is the number an iOS app icon carries. A badge is read without being opened and never
/// explained, so it has to mean one thing only: how many questions are waiting to be answered.
/// </remarks>
public interface IWaitingConversationCount
{
    /// <summary>Counts the conversations with a question waiting for a person's answer.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <summary>Counts waiting conversations from the conversation store.</summary>
/// <remarks>
/// The store answers "which conversations hold a pending question" with one indexed query over two
/// columns, and each answer is then read by id. That is a keyed lookup per waiting conversation,
/// which sounds worse than it is: a pending question blocks the run that asked it, so the waiting
/// set is small by construction - a handful, not a page.
/// <para>
/// Listing every conversation and intersecting would be one query fewer, but
/// <c>GetSummariesAsync</c> is fenced to the stores and a single controller (P9-G, #661): all other
/// listing must preserve owner-plus-participant semantics through
/// <see cref="IConversationStore.ListForCitizenAsync"/>. A count has no citizen to be relative to,
/// so it reads the conversations it already has ids for instead of widening that fence.
/// </para>
/// <para>
/// What is excluded is what a person could not act on: an archived thread, two agents waiting on
/// each other, something the conversation list does not show, and a checkpoint left behind by a
/// conversation that no longer exists. Each would be a number that sends someone into the app to
/// find nothing to answer.
/// </para>
/// </remarks>
public sealed class WaitingConversationCount(IConversationStore conversations) : IWaitingConversationCount
{
    private readonly IConversationStore _conversations = conversations;

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        var waiting = await _conversations.GetPendingAskUserCheckpointsAsync(ct).ConfigureAwait(false);

        // The common case on a quiet gateway, and it costs the lookups nothing to skip.
        if (waiting.Count == 0)
            return 0;

        var count = 0;

        foreach (var checkpoint in waiting)
        {
            var conversation = await _conversations
                .GetAsync(checkpoint.ConversationId, ct)
                .ConfigureAwait(false);

            if (IsWaitingOnAPerson(conversation))
                count++;
        }

        return count;
    }

    private static bool IsWaitingOnAPerson(Conversation? conversation) =>
        conversation is
        {
            Status: ConversationStatus.Active,
            Kind: ConversationKind.HumanAgent,
            Visibility: ConversationVisibility.UserFacing,
        };
}
