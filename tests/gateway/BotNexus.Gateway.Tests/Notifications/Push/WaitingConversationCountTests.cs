using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Notifications.Push;
using Moq;

namespace BotNexus.Gateway.Tests.Notifications.Push;

/// <summary>
/// Pins what the number on an iOS app icon counts (#168).
/// </summary>
/// <remarks>
/// A badge is read at a glance and never explained, so it has to mean exactly one thing: how many
/// conversations are waiting on this person right now. A count that included an archived thread, or
/// two agents waiting on each other, would send someone into the app to find nothing to answer.
/// </remarks>
public sealed class WaitingConversationCountTests
{
    private const string PendingJson = """{"prompt":"Which one?"}""";

    private static Conversation Thread(
        string id,
        ConversationStatus status = ConversationStatus.Active,
        ConversationKind kind = ConversationKind.HumanAgent,
        ConversationVisibility visibility = ConversationVisibility.UserFacing) =>
        new()
        {
            ConversationId = ConversationId.From(id),
            AgentId = AgentId.From("assistant"),
            Status = status,
            Kind = kind,
            Visibility = visibility,
        };

    /// <summary>A store where the named conversations hold a pending question.</summary>
    private static IWaitingConversationCount Counter(
        IEnumerable<string> waitingIds,
        params Conversation[] threads)
    {
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetPendingAskUserCheckpointsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. waitingIds.Select(id => new PendingAskUserCheckpoint(ConversationId.From(id), PendingJson))]);

        foreach (var thread in threads)
        {
            store.Setup(s => s.GetAsync(thread.ConversationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(thread);
        }

        // Anything the test did not lay down is a conversation the store no longer has.
        store.Setup(s => s.GetAsync(
                It.Is<ConversationId>(id => !threads.Any(t => t.ConversationId.Equals(id))),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null);

        return new WaitingConversationCount(store.Object);
    }

    [Fact]
    public async Task Counts_a_conversation_waiting_on_the_person()
    {
        var counter = Counter(["c1", "c2"], Thread("c1"), Thread("c2"), Thread("c3"));

        Assert.Equal(2, await counter.CountAsync());
    }

    [Fact]
    public async Task Ignores_a_conversation_nobody_is_waiting_in()
    {
        var counter = Counter(["c1"], Thread("c1"), Thread("c2"), Thread("c3"));

        Assert.Equal(1, await counter.CountAsync());
    }

    // Archiving a conversation stops it being answerable, so it must stop being counted.
    [Fact]
    public async Task Ignores_an_archived_conversation()
    {
        var counter = Counter(["c1"], Thread("c1", status: ConversationStatus.Archived));

        Assert.Equal(0, await counter.CountAsync());
    }

    // Two agents waiting on each other is not a person's queue.
    [Fact]
    public async Task Ignores_two_agents_waiting_on_each_other()
    {
        var counter = Counter(["c1"], Thread("c1", kind: ConversationKind.AgentAgent));

        Assert.Equal(0, await counter.CountAsync());
    }

    // Neither a hidden conversation nor one shown read-only is something a person can answer.
    [Theory]
    [InlineData(ConversationVisibility.InternalHidden)]
    [InlineData(ConversationVisibility.InspectableReadOnly)]
    public async Task Ignores_a_conversation_that_cannot_be_answered(ConversationVisibility visibility)
    {
        var counter = Counter(["c1"], Thread("c1", visibility: visibility));

        Assert.Equal(0, await counter.CountAsync());
    }

    // A pending prompt on a conversation the store no longer lists is a checkpoint left behind by a
    // deleted conversation: countable only as a number nobody can act on.
    [Fact]
    public async Task Ignores_a_pending_prompt_with_no_conversation_left()
    {
        var counter = Counter(["c1", "gone"], Thread("c1"));

        Assert.Equal(1, await counter.CountAsync());
    }

    [Fact]
    public async Task Counts_nothing_when_nobody_is_waiting()
    {
        var counter = Counter([], Thread("c1"), Thread("c2"));

        Assert.Equal(0, await counter.CountAsync());
    }
}
