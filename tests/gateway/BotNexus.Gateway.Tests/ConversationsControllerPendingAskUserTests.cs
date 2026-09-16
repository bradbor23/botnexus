using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the pending <c>ask_user</c> REST endpoint on <see cref="ConversationsController"/>
/// (#1488, ask_user durability Step 1/5). A reloaded, newly-opened, or mobile client that missed
/// the live <c>UserInputRequired</c> event hydrates the inline prompt from this endpoint, so it must
/// return the persisted payload, 204 when nothing is pending, and 404 when the conversation is unknown.
/// </summary>
public sealed class ConversationsControllerPendingAskUserTests
{
    private const string AgentId = "test-agent";
    private static readonly ConversationId TestConversationId = ConversationId.From("c_ask_test");

    private const string PersistedPrompt =
        "{\"requestId\":\"req-1\",\"conversationId\":\"c_ask_test\",\"prompt\":\"Continue?\",\"inputType\":\"FreeForm\"}";

    [Fact]
    public async Task GetPendingAskUser_WhenPromptPending_ReturnsPayload()
    {
        var (controller, store) = CreateController();
        var conversation = (await store.GetAsync(TestConversationId))!;
        conversation.PendingAskUserJson = PersistedPrompt;
        await store.SaveAsync(conversation);

        var result = await controller.GetPendingAskUser(AgentId, TestConversationId.Value, CancellationToken.None);

        var content = result.ShouldBeOfType<ContentResult>();
        content.Content.ShouldBe(PersistedPrompt);
        content.ContentType.ShouldBe("application/json");
    }

    [Fact]
    public async Task GetPendingAskUser_WhenNonePending_Returns204()
    {
        var (controller, _) = CreateController();

        var result = await controller.GetPendingAskUser(AgentId, TestConversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetPendingAskUser_NonExistentConversation_Returns404()
    {
        var (controller, _) = CreateController();

        var result = await controller.GetPendingAskUser(AgentId, "c_nonexistent", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    // ── answering from a notification (#168) ─────────────────────────────────

    // Telegram answers a question from the notification itself. Until now the only way to answer at
    // all was the live SignalR hub, so the app had to be open and connected — which is precisely
    // what someone looking at a lock screen is not.
    [Fact]
    public async Task Answer_WhenChoiceSubmitted_ResolvesTheQuestion()
    {
        var resolver = new StubResolver(AskUserResolutionResult.Resolved("req-1"));
        var (controller, _) = CreateController(resolver);

        var result = await controller.Answer(
            TestConversationId.Value,
            new AskUserAnswerRequest { RequestId = "req-1", SelectedValues = ["now"] },
            CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        resolver.Seen.ShouldNotBeNull();
        resolver.Seen!.ConversationId.Value.ShouldBe(TestConversationId.Value);
        resolver.Seen.RequestId.ShouldBe("req-1");
        resolver.Seen.SelectedValues.ShouldBe(["now"]);
    }

    [Fact]
    public async Task Answer_WhenTyped_CarriesTheText()
    {
        var resolver = new StubResolver(AskUserResolutionResult.Resolved("req-1"));
        var (controller, _) = CreateController(resolver);

        var result = await controller.Answer(
            TestConversationId.Value,
            new AskUserAnswerRequest { RequestId = "req-1", FreeFormText = "wait for the window" },
            CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        resolver.Seen!.FreeFormText.ShouldBe("wait for the window");
    }

    // Attribution only — the resolver logs where an answer came from — but a wrong label makes the
    // one log line that explains a resolved prompt lie about it.
    [Fact]
    public async Task Answer_SaysItCameOverRest()
    {
        var resolver = new StubResolver(AskUserResolutionResult.Resolved("req-1"));
        var (controller, _) = CreateController(resolver);

        await controller.Answer(
            TestConversationId.Value,
            new AskUserAnswerRequest { RequestId = "req-1", SelectedValues = ["now"] },
            CancellationToken.None);

        resolver.Seen!.OriginChannel?.Value.ShouldBe("rest");
    }

    // An empty submission is refused by the shared seam for every channel alike; the route reports
    // that rather than swallowing it.
    [Fact]
    public async Task Answer_WhenNothingWasAnswered_Returns400()
    {
        var resolver = new StubResolver(AskUserResolutionResult.InvalidSubmission("nothing was answered"));
        var (controller, _) = CreateController(resolver);

        var result = await controller.Answer(
            TestConversationId.Value,
            new AskUserAnswerRequest { RequestId = "req-1" },
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    // Answered in the portal a moment ago, or expired: the notification on the phone is stale, and
    // saying so beats pretending the tap worked.
    [Fact]
    public async Task Answer_WhenNothingIsPending_Returns404()
    {
        var resolver = new StubResolver(AskUserResolutionResult.NoPendingPrompt("nothing pending"));
        var (controller, _) = CreateController(resolver);

        var result = await controller.Answer(
            TestConversationId.Value,
            new AskUserAnswerRequest { RequestId = "req-1", SelectedValues = ["now"] },
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // The request id is what ties an answer to the question that was asked. Without it a stale
    // notification could answer whatever happens to be pending now.
    [Fact]
    public async Task Answer_WithoutARequestId_Returns400_AndAsksNothing()
    {
        var resolver = new StubResolver(AskUserResolutionResult.Resolved("req-1"));
        var (controller, _) = CreateController(resolver);

        var result = await controller.Answer(
            TestConversationId.Value,
            new AskUserAnswerRequest { FreeFormText = "yes" },
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        resolver.Seen.ShouldBeNull();
    }

    [Fact]
    public async Task Answer_ForAnUnknownConversation_Returns404()
    {
        var resolver = new StubResolver(AskUserResolutionResult.Resolved("req-1"));
        var (controller, _) = CreateController(resolver);

        var result = await controller.Answer(
            "c_nonexistent",
            new AskUserAnswerRequest { RequestId = "req-1", SelectedValues = ["now"] },
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
        resolver.Seen.ShouldBeNull();
    }

    // A gateway wired without the seam cannot answer, and says so rather than reporting success.
    [Fact]
    public async Task Answer_WithoutAResolver_Returns404()
    {
        var (controller, _) = CreateController();

        var result = await controller.Answer(
            TestConversationId.Value,
            new AskUserAnswerRequest { RequestId = "req-1", SelectedValues = ["now"] },
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    /// <summary>Stands in for the seam every channel answers a prompt through (#2322).</summary>
    private sealed class StubResolver(AskUserResolutionResult result) : IAskUserPromptResolver
    {
        public AskUserSubmission? Seen { get; private set; }

        public ValueTask<AskUserResolutionResult> ResolveAsync(
            AskUserSubmission submission,
            CancellationToken cancellationToken = default)
        {
            Seen = submission;

            return ValueTask.FromResult(result);
        }

        public bool TryGetPendingRequestId(ConversationId conversationId, out string requestId)
        {
            requestId = "req-1";

            return true;
        }
    }

    private static (ConversationsController, InMemoryConversationStore) CreateController(
        IAskUserPromptResolver? resolver = null)
    {
        var store = new InMemoryConversationStore();
        store.CreateAsync(new Conversation
        {
            ConversationId = TestConversationId,
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentId),
            Title = "Test Ask Conversation",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }).GetAwaiter().GetResult();

        var sessions = new InMemorySessionStore();
        var controller = new ConversationsController(store, sessions, askUserPromptResolver: resolver);
        return (controller, store);
    }
}
