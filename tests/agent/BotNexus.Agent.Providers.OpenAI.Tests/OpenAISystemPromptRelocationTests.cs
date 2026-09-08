using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.OpenAI.Tests;

/// <summary>
/// The volatile half of the system prompt must leave the system turn and land at the end of the
/// conversation on the OpenAI-shaped paths too.
///
/// <para>
/// These providers match the longest identical prefix rather than reading explicit breakpoints,
/// which is exactly why appending at the very end is safe: dropping a suffix leaves a valid
/// prefix, so the next request — carrying different relocated text — still matches everything up
/// to where this request's text began. Left in the system turn instead, the same change
/// invalidates every message behind it.
/// </para>
///
/// <para>
/// Both APIs are covered because they spell a text content part differently — Responses uses
/// <c>input_text</c>, Chat Completions uses <c>text</c> — and sending the wrong one is rejected.
/// </para>
/// </summary>
public class OpenAISystemPromptRelocationTests
{
    private const string Marker = SystemPromptPartition.BoundaryMarker;
    private const string Stable = "You are helpful.";
    private const string Volatile = "## Dynamic Project Context\nHEARTBEAT.md says hello.";

    [Fact]
    public async Task Responses_SendsOnlyTheStableHalfAsTheSystemTurn()
    {
        var body = await CaptureResponsesAsync($"{Stable}{Marker}{Volatile}");

        var first = body.RootElement.GetProperty("input")[0];
        first.GetProperty("role").GetString().ShouldBeOneOf("system", "developer");
        first.GetProperty("content").GetString().ShouldBe(Stable);
    }

    [Fact]
    public async Task Responses_RelocatesTheVolatileHalfToTheEndOfTheInput()
    {
        var body = await CaptureResponsesAsync($"{Stable}{Marker}{Volatile}");

        var input = body.RootElement.GetProperty("input");
        var lastItem = input[input.GetArrayLength() - 1];
        lastItem.GetProperty("role").GetString().ShouldBe("user");

        var text = LastPartText(lastItem);
        text.ShouldContain(Volatile);
        text.ShouldContain("not written by the user");
    }

    [Fact]
    public async Task Responses_WithoutAMarker_SendsThePromptUnchanged()
    {
        var body = await CaptureResponsesAsync(Stable);

        body.RootElement.GetProperty("input")[0].GetProperty("content").GetString().ShouldBe(Stable);
        LastPartText(Last(body.RootElement.GetProperty("input"))).ShouldNotContain("not written by the user");
    }

    [Fact]
    public async Task Completions_SendsOnlyTheStableHalfAsTheSystemMessage()
    {
        var body = await CaptureCompletionsAsync($"{Stable}{Marker}{Volatile}");

        var first = body.RootElement.GetProperty("messages")[0];
        first.GetProperty("role").GetString().ShouldBeOneOf("system", "developer");
        MessageText(first).ShouldBe(Stable);
    }

    [Fact]
    public async Task Completions_RelocatesTheVolatileHalfToTheLastMessage()
    {
        var body = await CaptureCompletionsAsync($"{Stable}{Marker}{Volatile}");

        var last = Last(body.RootElement.GetProperty("messages"));
        last.GetProperty("role").GetString().ShouldBe("user");

        var text = MessageText(last);
        text.ShouldContain("hello");     // the user's own words survive
        text.ShouldContain(Volatile);
        text.ShouldContain("not written by the user");
    }

    [Fact]
    public async Task Completions_WithoutAMarker_SendsThePromptUnchanged()
    {
        var body = await CaptureCompletionsAsync(Stable);

        MessageText(body.RootElement.GetProperty("messages")[0]).ShouldBe(Stable);
        MessageText(Last(body.RootElement.GetProperty("messages"))).ShouldNotContain("not written by the user");
    }

    // ---------- helpers ----------

    private static JsonElement Last(JsonElement array) => array[array.GetArrayLength() - 1];

    /// <summary>Content is either a plain string or an array of typed parts, depending on the path.</summary>
    private static string MessageText(JsonElement message)
    {
        var content = message.GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : LastPartText(message);
    }

    private static string LastPartText(JsonElement message)
    {
        var content = message.GetProperty("content");
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString()!;

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text))
                builder.AppendLine(text.GetString());
        }

        return builder.ToString();
    }

    private static async Task<JsonDocument> CaptureResponsesAsync(string systemPrompt)
    {
        var handler = new CapturingHandler("""
            data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}
            data: [DONE]
            """);

        var provider = new OpenAIResponsesProvider(
            new HttpClient(handler), NullLogger<OpenAIResponsesProvider>.Instance);

        var stream = provider.Stream(
            TestHelpers.MakeModel(id: "gpt-5.4", api: "openai-responses", reasoning: true),
            TestHelpers.MakeContext(systemPrompt),
            new OpenAIResponsesOptions { ApiKey = "test-key" });

        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        return JsonDocument.Parse(handler.LastRequestBody!);
    }

    private static async Task<JsonDocument> CaptureCompletionsAsync(string systemPrompt)
    {
        var handler = new CapturingHandler("""
            data: {"choices":[{"delta":{"content":"ok"},"finish_reason":null}]}
            data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            data: [DONE]
            """);

        var provider = new OpenAICompletionsProvider(
            new HttpClient(handler), NullLogger<OpenAICompletionsProvider>.Instance);

        var stream = provider.Stream(
            TestHelpers.MakeModel(id: "gpt-4.1", api: "openai-completions", reasoning: false),
            TestHelpers.MakeContext(systemPrompt),
            new OpenAICompletionsOptions { ApiKey = "test-key" });

        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        return JsonDocument.Parse(handler.LastRequestBody!);
    }

    private sealed class CapturingHandler(string ssePayload) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
