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

        SystemPromptPartition.TryAppendToConversation(messages, "runtime state").ShouldBeTrue();

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

        SystemPromptPartition.TryAppendToConversation(messages, "runtime state").ShouldBeTrue();

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

        SystemPromptPartition.TryAppendToConversation(messages, "runtime state").ShouldBeFalse();
        ((List<object>)messages[^1]["content"]!).Count.ShouldBe(1);
    }

    [Fact]
    public void Append_RefusesWhenThereIsNoConversation()
    {
        SystemPromptPartition.TryAppendToConversation([], "runtime state").ShouldBeFalse();
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

        SystemPromptPartition.TryAppendToConversation(messages, volatileText).ShouldBeFalse();
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

        SystemPromptPartition.TryAppendToConversation(messages, "runtime state").ShouldBeFalse();
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
