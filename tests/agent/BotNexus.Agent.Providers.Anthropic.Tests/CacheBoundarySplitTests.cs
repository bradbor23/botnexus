using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Where the volatile half of the system prompt ends up (BOTNEXUS_CACHE_BOUNDARY, issue #806).
///
/// <para>
/// It used to become a second, unstamped system block. That protected the stable half of the
/// prompt and nothing else: every provider builds its prefix as tools, then system, then messages,
/// so a volatile block inside <c>system</c> sits in front of the entire conversation and
/// invalidates all of it the moment a watched file changes.
/// </para>
///
/// <para>
/// It now moves out of the system prompt and onto the end of the conversation, behind the last
/// cache breakpoint. That ordering is the whole trick and is easy to get backwards: the volatile
/// text is rebuilt per request and never persisted, so placed anywhere inside a cached prefix it
/// would guarantee the NEXT request cannot match -- turning the fix into a miss on every turn.
/// </para>
/// </summary>
public class CacheBoundarySplitTests
{
    private const string Marker = "<!-- BOTNEXUS_CACHE_BOUNDARY -->";

    [Fact]
    public void VolatileTail_LeavesTheSystemPromptEntirely()
    {
        var stable = "You are helpful.\nFollow instructions.";
        var volatileTail = "## Memory\nToday is Monday.";

        var body = Build($"{stable}\n{Marker}\n{volatileTail}");

        var system = body["system"]!.AsArray();
        system.Count.ShouldBe(1);
        system[0]!["text"]!.GetValue<string>().ShouldBe(stable);
        system[0]!.AsObject().ContainsKey("cache_control").ShouldBeTrue();
    }

    [Fact]
    public void VolatileTail_ArrivesOnTheLastMessage_BehindTheBreakpoint()
    {
        var volatileTail = "## Memory\nToday is Monday.";

        var body = Build($"You are helpful.\n{Marker}\n{volatileTail}");

        var blocks = body["messages"]!.AsArray()[^1]!["content"]!.AsArray();
        blocks.Count.ShouldBe(2);

        // The user's own words keep the breakpoint: that is the entry the next request reads.
        blocks[0]!.AsObject().ContainsKey("cache_control").ShouldBeTrue();

        // The relocated context follows it, outside every cached prefix.
        var relocated = blocks[1]!.AsObject();
        relocated["text"]!.GetValue<string>().ShouldContain(volatileTail);
        relocated.ContainsKey("cache_control").ShouldBeFalse();
    }

    [Fact]
    public void RelocatedContext_SaysWhereItCameFrom()
    {
        // It is being moved into a user turn, and it can carry watched file contents. The model
        // must not read those as something the user just typed.
        var body = Build($"You are helpful.\n{Marker}\nHEARTBEAT.md contents");

        var blocks = body["messages"]!.AsArray()[^1]!["content"]!.AsArray();
        blocks[^1]!["text"]!.GetValue<string>()
            .ShouldContain("not written by the user", Case.Insensitive);
    }

    [Fact]
    public void WithoutBoundary_TheWholePromptStaysStableAndCached()
    {
        var systemPrompt = "You are a helpful assistant.";

        var body = Build(systemPrompt);

        var system = body["system"]!.AsArray();
        system.Count.ShouldBe(1);
        system[0]!["text"]!.GetValue<string>().ShouldBe(systemPrompt);
        system[0]!.AsObject().ContainsKey("cache_control").ShouldBeTrue();

        // Nothing was relocated, so the message keeps only its own content.
        body["messages"]!.AsArray()[^1]!["content"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void CacheRetentionNone_StillRelocates()
    {
        // Relocation is about where volatile text sits in the prefix, which matters to providers
        // that cache implicitly too. It must not be tied to whether we place breakpoints.
        var body = Build($"Stable\n{Marker}\nVolatile", CacheRetention.None);

        body["system"]!.AsArray().Count.ShouldBe(1);
        var blocks = body["messages"]!.AsArray()[^1]!["content"]!.AsArray();
        blocks[^1]!["text"]!.GetValue<string>().ShouldContain("Volatile");
        blocks[^1]!.AsObject().ContainsKey("cache_control").ShouldBeFalse();
    }

    [Fact]
    public void LongRetention_TtlStaysOnTheStableBlock()
    {
        var body = Build($"Stable prefix\n{Marker}\nVolatile tail", CacheRetention.Long);

        var stableBlock = body["system"]!.AsArray()[0]!.AsObject();
        stableBlock["cache_control"]!["ttl"]!.GetValue<string>().ShouldBe("1h");
    }

    [Fact]
    public void OAuth_KeepsItsPreambleAndStillRelocates()
    {
        var body = Build($"Stable prefix\n{Marker}\nVolatile tail", isOAuthToken: true);

        var system = body["system"]!.AsArray();
        system.Count.ShouldBe(2);
        system[0]!["text"]!.GetValue<string>().ShouldContain("Claude Code");
        system[1]!["text"]!.GetValue<string>().ShouldBe("Stable prefix");

        body["messages"]!.AsArray()[^1]!["content"]!.AsArray()[^1]!["text"]!.GetValue<string>()
            .ShouldContain("Volatile tail");
    }

    [Fact]
    public void EmptyVolatileTail_ChangesNothing()
    {
        var body = Build($"Stable prefix\n{Marker}\n");

        body["system"]!.AsArray().Count.ShouldBe(1);
        body["system"]!.AsArray()[0]!["text"]!.GetValue<string>().ShouldBe("Stable prefix");
        body["messages"]!.AsArray()[^1]!["content"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void EmptyStableHalf_SendsNoSystemBlockAtAll()
    {
        // A prompt that is entirely volatile leaves nothing stable to send. Emitting the block
        // anyway would put an empty text block on the wire, which the API rejects.
        var body = Build($"\n{Marker}\nVolatile content only");

        body["system"].ShouldBeNull();
        body["messages"]!.AsArray()[^1]!["content"]!.AsArray()[^1]!["text"]!.GetValue<string>()
            .ShouldContain("Volatile content only");
    }

    [Fact]
    public void NoConversationToAppendTo_KeepsTheContextInTheSystemPrompt()
    {
        // Losing context is far worse than losing a cache prefix, so with nowhere safe to put it
        // the old placement stands.
        var body = Build($"Stable prefix\n{Marker}\nVolatile tail", messages: []);

        var system = body["system"]!.AsArray();
        system.Count.ShouldBe(2);
        system[1]!["text"]!.GetValue<string>().ShouldBe("Volatile tail");
        system[1]!.AsObject().ContainsKey("cache_control").ShouldBeFalse();
    }

    private static System.Text.Json.Nodes.JsonObject Build(
        string systemPrompt,
        CacheRetention retention = CacheRetention.Short,
        bool isOAuthToken = false,
        IReadOnlyList<Message>? messages = null)
        => AnthropicRequestBuilder.BuildRequestBody(
            TestHelpers.MakeModel(),
            new Context(SystemPrompt: systemPrompt, Messages: messages ?? [MakeUserMessage()]),
            new StreamOptions { ApiKey = "sk-ant-test", CacheRetention = retention },
            null,
            isOAuthToken,
            _ => false);

    private static UserMessage MakeUserMessage() =>
        new(new UserMessageContent("hello"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
