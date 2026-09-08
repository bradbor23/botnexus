using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;

namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// Splitting the system prompt and relocating its volatile half.
///
/// Every provider builds its cache prefix as tools, then system, then messages, so volatile text
/// inside <c>system</c> sits in front of the whole conversation and invalidates all of it whenever
/// it changes. Moving it to the end of the conversation is only a fix if it lands behind the last
/// breakpoint — the text is rebuilt per request and never persisted, so inside a cached prefix it
/// would guarantee the next request misses.
/// </summary>
public class SystemPromptPartitionTests
{
    private const string Marker = SystemPromptPartition.BoundaryMarker;

    [Fact]
    public void Split_WithoutMarker_TreatsTheWholePromptAsStable()
    {
        var (stable, volatileTail) = SystemPromptPartition.Split("You are helpful.");

        stable.ShouldBe("You are helpful.");
        volatileTail.ShouldBeNull();
    }

    [Fact]
    public void Split_TrimsBothHalves()
    {
        var (stable, volatileTail) = SystemPromptPartition.Split($"Stable{Marker}Volatile");

        stable.ShouldBe("Stable");
        volatileTail.ShouldBe("Volatile");
    }

    [Fact]
    public void Split_EmptyVolatileHalf_IsNoHalfAtAll()
    {
        var (stable, volatileTail) = SystemPromptPartition.Split($"Stable{Marker}   ");

        stable.ShouldBe("Stable");
        volatileTail.ShouldBeNull();
    }

    [Fact]
    public void Split_MarkerWithNothingUsableEitherSide_FallsBackToTheWholePrompt()
    {
        var prompt = $"   {Marker}   ";

        var (stable, volatileTail) = SystemPromptPartition.Split(prompt);

        stable.ShouldBe(prompt);
        volatileTail.ShouldBeNull();
    }

    [Fact]
    public void Append_LandsAfterAnExistingBreakpoint()
    {
        // The invariant the whole change rests on. Placed before the breakpoint, the relocated
        // text joins the cached prefix and the next request cannot match it.
        var messages = new List<Dictionary<string, object?>>
        {
            UserMessage(WithBreakpoint(TextBlock("hello")))
        };

        SystemPromptPartition.TryAppendToBlockConversation(messages, "runtime state").ShouldBeTrue();

        var blocks = (List<object>)messages[^1]["content"]!;
        blocks.Count.ShouldBe(2);
        ((Dictionary<string, object?>)blocks[0]).ContainsKey("cache_control").ShouldBeTrue();
        ((Dictionary<string, object?>)blocks[1]).ContainsKey("cache_control").ShouldBeFalse();
    }

    [Fact]
    public void Append_WrapsPlainStringContentWithoutLosingIt()
    {
        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "user", ["content"] = "hello" }
        };

        SystemPromptPartition.TryAppendToBlockConversation(messages, "runtime state").ShouldBeTrue();

        var blocks = (List<object>)messages[^1]["content"]!;
        ((Dictionary<string, object?>)blocks[0])["text"].ShouldBe("hello");
        ((Dictionary<string, object?>)blocks[1])["text"]!.ToString()!.ShouldContain("runtime state");
    }

    [Fact]
    public void Append_RefusesAnAssistantTurn()
    {
        // Appending here would put supplied context into the model's own words.
        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "assistant", ["content"] = new List<object> { TextBlock("sure") } }
        };

        SystemPromptPartition.TryAppendToBlockConversation(messages, "runtime state").ShouldBeFalse();
        ((List<object>)messages[^1]["content"]!).Count.ShouldBe(1);
    }

    [Fact]
    public void Append_RefusesWhenThereIsNoConversation()
    {
        SystemPromptPartition.TryAppendToBlockConversation([], "runtime state").ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Append_IgnoresAnEmptyPayload(string? volatileText)
    {
        var messages = new List<Dictionary<string, object?>>
        {
            UserMessage(TextBlock("hello"))
        };

        SystemPromptPartition.TryAppendToBlockConversation(messages, volatileText).ShouldBeFalse();
        ((List<object>)messages[^1]["content"]!).Count.ShouldBe(1);
    }

    [Fact]
    public void Append_RefusesAnUnrecognisedContentShape()
    {
        // Declining costs a cache prefix; guessing could drop the message's real content.
        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "user", ["content"] = null }
        };

        SystemPromptPartition.TryAppendToBlockConversation(messages, "runtime state").ShouldBeFalse();
    }

    // ---------- OpenAI-shaped conversations ----------

    [Fact]
    public void AppendText_KeepsStringContentAString()
    {
        // Strict OpenAI-compatible servers reject the parts-array form. A size optimisation must
        // not change the wire shape under them.
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "hello" }
        };

        SystemPromptPartition.TryAppendToTextConversation(messages, "runtime state").ShouldBeTrue();

        var content = messages[0]!["content"]!.GetValue<string>();
        content.ShouldStartWith("hello");
        content.ShouldContain("runtime state");
    }

    [Theory]
    [InlineData("text")]        // Chat Completions
    [InlineData("input_text")]  // Responses
    public void AppendText_ClonesThePartTypeAlreadyInUse(string partType)
    {
        // The two APIs spell a text part differently and reject the other spelling.
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray { new JsonObject { ["type"] = partType, ["text"] = "hello" } }
            }
        };

        SystemPromptPartition.TryAppendToTextConversation(messages, "runtime state").ShouldBeTrue();

        var parts = messages[0]!["content"]!.AsArray();
        parts.Count.ShouldBe(2);
        parts[1]!["type"]!.GetValue<string>().ShouldBe(partType);
    }

    [Fact]
    public void AppendText_DeclinesWhenNoTextPartRevealsTheSpelling()
    {
        // Guessing would be rejected by the API. Falling back to the system prompt costs a cache
        // prefix and nothing else.
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "input_image" } }
            }
        };

        SystemPromptPartition.TryAppendToTextConversation(messages, "runtime state").ShouldBeFalse();
    }

    [Fact]
    public void AppendText_RefusesAnAssistantTurn()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "assistant", ["content"] = "sure" }
        };

        SystemPromptPartition.TryAppendToTextConversation(messages, "runtime state").ShouldBeFalse();
    }

    [Fact]
    public void AppendText_RefusesWhenThereIsNoConversation()
    {
        SystemPromptPartition.TryAppendToTextConversation([], "runtime state").ShouldBeFalse();
    }

    private static Dictionary<string, object?> TextBlock(string text)
        => new() { ["type"] = "text", ["text"] = text };

    private static Dictionary<string, object?> WithBreakpoint(Dictionary<string, object?> block)
    {
        block["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" };
        return block;
    }

    private static Dictionary<string, object?> UserMessage(Dictionary<string, object?> block)
        => new() { ["role"] = "user", ["content"] = new List<object> { block } };
}
