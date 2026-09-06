using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The three rules that decide what the conversation switcher can reach - visibility,
/// archived-ness and the typed query - plus the read-only marker. Pure model tests: no render, so a
/// change to these rules fails here rather than in a component test that could pass for the wrong
/// reason.
/// </summary>
public sealed class ConversationSwitcherModelTests
{
    private static ConversationState Conv(
        string id,
        string title,
        bool pinned = false,
        string status = "Active",
        ConversationSource source = ConversationSource.Channel,
        ConversationKind kind = ConversationKind.HumanAgent,
        ConversationVisibility visibility = ConversationVisibility.UserFacing) =>
        new()
        {
            ConversationId = id,
            Title = title,
            IsPinned = pinned,
            Status = status,
            Source = source,
            Kind = kind,
            Visibility = visibility,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static ConversationSwitcherView Build(IEnumerable<ConversationState> conversations, string? query = null) =>
        ConversationSwitcherModel.Build(conversations, SelectionSource.UserClick, null, query);

    [Fact]
    public void NoQuery_ReturnsEveryReachableConversation()
    {
        var view = Build([Conv("c-1", "Alpha"), Conv("c-2", "Beta")]);

        Assert.Equal(2, view.Count);
        Assert.False(view.IsEmpty);
    }

    [Fact]
    public void Query_MatchesTitleCaseInsensitively()
    {
        var view = Build([Conv("c-1", "Daily Cost Monitor"), Conv("c-2", "Skill Review")], "COST");

        Assert.Single(view.Flattened);
        Assert.Equal("c-1", view.Flattened[0].ConversationId);
    }

    [Fact]
    public void Query_MatchesMidTitleSubstring()
    {
        var view = Build([Conv("c-1", "Exact Reply Instruction Followed")], "reply inst");

        Assert.Single(view.Flattened);
    }

    [Fact]
    public void Query_IsTrimmedBeforeMatching()
    {
        var view = Build([Conv("c-1", "Alpha")], "  alpha  ");

        Assert.Single(view.Flattened);
    }

    [Fact]
    public void Query_WhitespaceOnly_IsTreatedAsNoFilter()
    {
        var view = Build([Conv("c-1", "Alpha"), Conv("c-2", "Beta")], "   ");

        Assert.Equal(2, view.Count);
    }

    [Fact]
    public void Query_DoesNotMatchConversationId()
    {
        // Ids are opaque server tokens. Matching them would surface a row whose visible title has
        // nothing to do with what the user typed.
        var view = Build([Conv("conv-abc123", "Alpha")], "abc123");

        Assert.True(view.IsEmpty);
    }

    [Fact]
    public void Query_MatchingNothing_YieldsEmptyView()
    {
        var view = Build([Conv("c-1", "Alpha")], "zzz");

        Assert.True(view.IsEmpty);
        Assert.Empty(view.Groups);
    }

    [Fact]
    public void InternalHiddenConversations_AreNeverListed()
    {
        // ForPicker partitions whatever it is handed; the visibility predicate is this model's job.
        var view = Build(
        [
            Conv("c-1", "Visible"),
            Conv("internal-1", "Bookkeeping", visibility: ConversationVisibility.InternalHidden),
        ]);

        Assert.Single(view.Flattened);
        Assert.Equal("c-1", view.Flattened[0].ConversationId);
    }

    [Fact]
    public void InspectableReadOnlyConversations_AreListed()
    {
        // Visible-but-not-writable is still somewhere the user can go.
        var view = Build([Conv("c-1", "Observer", visibility: ConversationVisibility.InspectableReadOnly)]);

        Assert.Single(view.Flattened);
    }

    [Fact]
    public void ArchivedConversations_AreNotListed()
    {
        var view = Build([Conv("c-1", "Live"), Conv("c-2", "Old", status: "Archived")]);

        Assert.Single(view.Flattened);
        Assert.Equal("c-1", view.Flattened[0].ConversationId);
    }

    [Fact]
    public void ArchivedStatus_IsMatchedIgnoringCase()
    {
        // Status is a free-form server string; casing was never promised.
        var view = Build([Conv("c-1", "Old", status: "archived")]);

        Assert.True(view.IsEmpty);
    }

    [Fact]
    public void FlattenedOrder_MatchesGroupRenderOrder()
    {
        // The keyboard highlight is an index into Flattened, so it MUST be the groups concatenated
        // in render order - otherwise arrow-down highlights one row and Enter opens another.
        var view = Build(
        [
            Conv("c-normal", "Normal"),
            Conv("c-pinned", "Pinned One", pinned: true),
            Conv("c-cron", "Nightly", source: ConversationSource.Cron),
        ]);

        var expected = view.Groups.SelectMany(g => g.Conversations.Select(c => c.ConversationId)).ToList();
        Assert.Equal(expected, view.Flattened.Select(c => c.ConversationId).ToList());
    }

    [Fact]
    public void Grouping_IsDelegatedToTheSharedPickerPartition()
    {
        var view = Build(
        [
            Conv("c-normal", "Normal"),
            Conv("c-pinned", "Pinned One", pinned: true),
            Conv("c-cron", "Nightly", source: ConversationSource.Cron),
            Conv("c-hook", "Hooked", source: ConversationSource.Webhook),
        ]);

        var expectedLabels = new List<string>
        {
            PortalConversationGrouping.PinnedLabel,
            PortalConversationGrouping.ConversationsLabel,
            PortalConversationGrouping.ScheduledLabel,
            PortalConversationGrouping.WebhooksLabel,
        };

        Assert.Equal(expectedLabels, view.Groups.Select(g => g.Label).ToList());
    }

    [Fact]
    public void Grouping_DropsEmptyGroups()
    {
        var view = Build([Conv("c-1", "Only A Normal One")]);

        Assert.Single(view.Groups);
        Assert.Equal(PortalConversationGrouping.ConversationsLabel, view.Groups[0].Label);
    }

    [Fact]
    public void ReadOnlyRow_TrueForUnattendedConversationInTheNormalGroup()
    {
        // A sub-agent transcript groups under "Conversations" beside ordinary chats, so without the
        // projection clause it would be indistinguishable from a writable one.
        var subAgent = Conv("c-1", "Sub-agent run", kind: ConversationKind.AgentSubAgent);

        Assert.True(ConversationSwitcherModel.IsReadOnlyRow(subAgent, PortalConversationGrouping.ConversationsLabel));
    }

    [Fact]
    public void ReadOnlyRow_TrueForCronAdoptedConversationViaItsGroup()
    {
        // Source is write-once (#2304): a channel-created conversation later adopted by a cron job
        // keeps Source=Channel forever, so only its Scheduled group membership reveals it.
        var adopted = Conv("c-1", "Adopted by cron", source: ConversationSource.Channel);

        Assert.False(adopted.Project(SelectionSource.UserClick).IsUnattended);
        Assert.True(ConversationSwitcherModel.IsReadOnlyRow(adopted, PortalConversationGrouping.ScheduledLabel));
    }

    [Fact]
    public void ReadOnlyRow_FalseForAnOrdinaryChat()
    {
        var chat = Conv("c-1", "Ordinary");

        Assert.False(ConversationSwitcherModel.IsReadOnlyRow(chat, PortalConversationGrouping.ConversationsLabel));
    }

    [Fact]
    public void ReadOnlyRow_FalseForAPinnedOrdinaryChat()
    {
        // Pinning must not imply read-only: the Pinned group holds ordinary writable chats.
        var chat = Conv("c-1", "Pinned ordinary", pinned: true);

        Assert.False(ConversationSwitcherModel.IsReadOnlyRow(chat, PortalConversationGrouping.PinnedLabel));
    }

    [Fact]
    public void Build_WithNoConversations_YieldsTheEmptyView()
    {
        var view = Build([]);

        Assert.True(view.IsEmpty);
        Assert.Empty(view.Groups);
        Assert.Equal(0, view.Count);
    }
}
