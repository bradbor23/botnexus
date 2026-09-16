using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Notifications.Push;
using Moq;

namespace BotNexus.Gateway.Tests.Notifications;

/// <summary>
/// Pins the lookup that puts an agent's question, and its answers, into a push (#168).
/// </summary>
/// <remarks>
/// The conversation row already holds the pending prompt as JSON — it is what lets a reloaded tab
/// rehydrate a question. Reading it at send time is why a notification can carry the choices without
/// the notification contract growing fields, and without a store migration for something the store
/// already keeps.
/// </remarks>
public sealed class PendingQuestionLookupTests
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string ThreadId = "c_1";

    private static string Json(
        string requestId = "req-1",
        bool allowFreeForm = true,
        bool allowMultiple = false,
        params AskUserChoice[] choices) =>
        JsonSerializer.Serialize(
            new AskUserRequest
            {
                RequestId = requestId,
                ConversationId = ConversationId.From(ThreadId),
                SessionId = SessionId.From("s-1"),
                AgentId = AgentId.From("assistant"),
                Prompt = "Deploy now, or wait?",
                Choices = choices.Length == 0 ? null : choices,
                AllowMultiple = allowMultiple,
                AllowFreeForm = allowFreeForm,
            },
            Wire);

    private static IPendingQuestionLookup Lookup(string? pendingJson)
    {
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(ConversationId.From(ThreadId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Conversation
            {
                ConversationId = ConversationId.From(ThreadId),
                AgentId = AgentId.From("assistant"),
                PendingAskUserJson = pendingJson,
            });

        return new PendingQuestionLookup(store.Object);
    }

    [Fact]
    public async Task Reads_the_request_and_its_choices()
    {
        var lookup = Lookup(Json(
            choices:
            [
                new AskUserChoice { Value = "now", Label = "Deploy now" },
                new AskUserChoice { Value = "wait", Label = "Wait" },
            ]));

        var question = await lookup.FindAsync(ConversationId.From(ThreadId));

        Assert.NotNull(question);
        Assert.Equal("req-1", question.RequestId);
        Assert.True(question.AllowFreeForm);
        Assert.Collection(
            question.Choices,
            first => { Assert.Equal("now", first.Value); Assert.Equal("Deploy now", first.Label); },
            second => { Assert.Equal("wait", second.Value); Assert.Equal("Wait", second.Label); });
    }

    // A free-form question has no buttons, and a phone should offer a text field instead of nothing.
    [Fact]
    public async Task Reads_a_question_that_has_no_choices()
    {
        var lookup = Lookup(Json());

        var question = await lookup.FindAsync(ConversationId.From(ThreadId));

        Assert.NotNull(question);
        Assert.Empty(question.Choices);
        Assert.True(question.AllowFreeForm);
    }

    [Fact]
    public async Task Finds_nothing_when_no_question_is_pending()
    {
        var lookup = Lookup(pendingJson: null);

        Assert.Null(await lookup.FindAsync(ConversationId.From(ThreadId)));
    }

    // A payload written by a newer gateway, or half-written by a crash, must cost the notification
    // its buttons and nothing more.
    [Fact]
    public async Task Finds_nothing_when_the_stored_question_cannot_be_read()
    {
        var lookup = Lookup("{ this is not json");

        Assert.Null(await lookup.FindAsync(ConversationId.From(ThreadId)));
    }

    [Fact]
    public async Task Finds_nothing_for_a_conversation_the_store_does_not_have()
    {
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null);

        Assert.Null(await new PendingQuestionLookup(store.Object).FindAsync(ConversationId.From(ThreadId)));
    }
}
