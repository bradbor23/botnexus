using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Generated agent avatars. Thirteen of sixteen agents on the reporting instance had no emoji, so
/// all thirteen drew the same default glyph and identity was carried by reading a name. These pin
/// the two properties that make a generated mark usable: it is STABLE for an agent, and it is
/// DIFFERENT between agents.
/// </summary>
public sealed class AgentAvatarTests
{
    // ---- hue: stability is the whole point ------------------------------------------------

    [Fact]
    public void The_same_agent_id_always_yields_the_same_hue()
    {
        Assert.Equal(AgentAvatarModel.HueFor("gantry-manager"), AgentAvatarModel.HueFor("gantry-manager"));
    }

    [Fact]
    public void The_hue_is_a_fixed_value_not_a_per_process_hash()
    {
        // Pinned literals. string.GetHashCode is randomised per process, so an implementation built
        // on it would pass an equality check within one run and still recolour every agent on the
        // next gateway restart. Only a hard-coded expectation catches that.
        Assert.Equal(207, AgentAvatarModel.HueFor("assistant"));
        Assert.Equal(356, AgentAvatarModel.HueFor("day-trader"));
    }

    [Fact]
    public void Every_hue_is_a_valid_degree()
    {
        foreach (var id in new[] { "", "a", "assistant", "harbor-relay", "🧭", new string('x', 300) })
            Assert.InRange(AgentAvatarModel.HueFor(id), 0, 359);
    }

    [Fact]
    public void Different_agents_mostly_get_different_hues()
    {
        // Not a uniqueness guarantee - 360 buckets will collide eventually - but a
        // floor: if a change made hues cluster, this is where it shows.
        // A fictional roster. This repository is public; the operator's real agent names are
        // exactly the "personal world data" the pre-commit guard exists to keep out of it.
        string[] roster =
        [
            "assistant", "day-trader", "atlas-scheduler", "beacon-monitor", "cargo-router",
            "delta-auditor", "ember-indexer", "frost-archivist", "gantry-manager",
            "harbor-relay", "ionise-reporter", "juniper-planner",
        ];

        var distinct = roster.Select(AgentAvatarModel.HueFor).Distinct().Count();

        Assert.True(distinct >= roster.Length - 1, $"Expected near-unique hues across the roster, got {distinct}/{roster.Length}.");
    }

    // ---- monogram -------------------------------------------------------------------------

    [Theory]
    [InlineData("Gantry Manager", "GM")]
    [InlineData("Harbor Relay Service", "HR")]
    [InlineData("day-trader", "DT")]
    [InlineData("cost_monitor", "CM")]
    [InlineData("juniper.planner", "JP")]
    public void Multi_word_names_take_one_initial_from_each_of_the_first_two_words(string name, string expected)
    {
        Assert.Equal(expected, AgentAvatarModel.Monogram(name, "id"));
    }

    [Fact]
    public void A_single_word_name_takes_two_letters_from_it()
    {
        // One character collides far too readily across a large roster.
        Assert.Equal("AS", AgentAvatarModel.Monogram("assistant", "id"));
    }

    [Fact]
    public void A_single_letter_name_yields_that_letter()
    {
        Assert.Equal("Q", AgentAvatarModel.Monogram("Q", "id"));
    }

    [Fact]
    public void Leading_emoji_and_punctuation_are_skipped()
    {
        // Taking the first char blindly would grab half a surrogate pair and render as a broken glyph.
        Assert.Equal("JP", AgentAvatarModel.Monogram("🧭 Juniper Planner", "id"));
        Assert.Equal("GM", AgentAvatarModel.Monogram("  Gantry   Manager ", "id"));
    }

    [Fact]
    public void Digits_count_as_initials()
    {
        Assert.Equal("A3", AgentAvatarModel.Monogram("agent 3", "id"));
    }

    [Fact]
    public void The_agent_id_is_used_when_the_name_is_empty()
    {
        Assert.Equal("HR", AgentAvatarModel.Monogram("", "harbor-relay"));
        Assert.Equal("HR", AgentAvatarModel.Monogram(null, "harbor-relay"));
        Assert.Equal("HR", AgentAvatarModel.Monogram("   ", "harbor-relay"));
    }

    [Fact]
    public void A_name_with_no_letters_or_digits_falls_back_to_the_id()
    {
        Assert.Equal("ID", AgentAvatarModel.Monogram("🤖", "identity"));
    }

    [Fact]
    public void Everything_unusable_yields_a_placeholder_rather_than_an_empty_chip()
    {
        Assert.Equal("?", AgentAvatarModel.Monogram("🤖", "———"));
        Assert.Equal("?", AgentAvatarModel.Monogram(null, null));
    }

    [Fact]
    public void Monograms_are_uppercased_invariantly()
    {
        Assert.Equal("DT", AgentAvatarModel.Monogram("day-trader", "id"));
    }

    // ---- resolution -----------------------------------------------------------------------

    [Fact]
    public void A_configured_emoji_wins_over_the_generated_monogram()
    {
        var avatar = AgentAvatarModel.For("juniper-planner", "Juniper Planner", "🧭");

        Assert.True(avatar.UsesEmoji);
        Assert.Equal("🧭", avatar.Emoji);
    }

    [Fact]
    public void An_agent_without_an_emoji_gets_a_monogram()
    {
        var avatar = AgentAvatarModel.For("gantry-manager", "Gantry Manager", null);

        Assert.False(avatar.UsesEmoji);
        Assert.Null(avatar.Emoji);
        Assert.Equal("GM", avatar.Monogram);
    }

    [Fact]
    public void Whitespace_is_not_an_emoji()
    {
        // A config field someone cleared should not leave the agent with a blank plate.
        var avatar = AgentAvatarModel.For("assistant", "assistant", "   ");

        Assert.False(avatar.UsesEmoji);
        Assert.Equal("AS", avatar.Monogram);
    }

    [Fact]
    public void An_emoji_is_trimmed()
    {
        Assert.Equal("🧭", AgentAvatarModel.For("a", "A", " 🧭 ").Emoji);
    }

    [Fact]
    public void A_monogram_is_always_one_or_two_characters()
    {
        foreach (var name in new[] { "assistant", "Gantry Manager", "a b c d e", "Q", "🤖", "" })
        {
            var monogram = AgentAvatarModel.For("some-agent", name, null).Monogram;
            Assert.InRange(monogram.Length, 1, 2);
        }
    }
}
