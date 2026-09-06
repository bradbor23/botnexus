namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// The rendered shape of the desktop conversation switcher: the grouped rows to draw, plus the
/// same rows flattened into the exact order they appear on screen.
/// </summary>
/// <param name="Groups">
/// Labelled groups in render order, already partitioned by <see cref="PortalConversationGrouping"/>.
/// Never contains an empty group.
/// </param>
/// <param name="Flattened">
/// Every conversation in <paramref name="Groups"/>, concatenated group by group in render order.
/// The keyboard highlight is an index into THIS list, which is why it is built here beside the
/// groups rather than re-derived by the component: a flatten computed separately from the render
/// could disagree with what the user sees, and arrow-down would then select a different row from
/// the highlighted one.
/// </param>
public sealed record ConversationSwitcherView(
    IReadOnlyList<PortalConversationGroup> Groups,
    IReadOnlyList<ConversationState> Flattened)
{
    /// <summary>An empty view - no groups, nothing to highlight.</summary>
    public static readonly ConversationSwitcherView Empty = new([], []);

    /// <summary>Total rows across all groups; the bound for a keyboard highlight index.</summary>
    public int Count => Flattened.Count;

    /// <summary>True when the query matched nothing (or the agent has no switchable conversations).</summary>
    public bool IsEmpty => Flattened.Count == 0;
}

/// <summary>
/// Builds the desktop conversation switcher's contents: which conversations are switchable, how a
/// typed query narrows them, and how the survivors group.
/// </summary>
/// <remarks>
/// <para>
/// This exists as a pure function rather than as logic inside the component so the three rules that
/// decide what a user can reach - visibility, archived-ness and the query match - are testable
/// without rendering, and so the switcher cannot drift from the sidebar's own notion of which
/// conversations exist. Grouping is <b>delegated</b> to <see cref="PortalConversationGrouping.ForPicker"/>,
/// the single client-wide partition the desktop sidebar and the mobile picker already share; this
/// type deliberately adds no fifth group and re-implements no precedence rule.
/// </para>
/// <para>
/// <b>Sections are intentionally not honoured.</b> The desktop sidebar subtracts a section-assigned
/// conversation from its default list and renders it inside the section instead. The switcher does
/// the opposite: a section-assigned conversation still appears here, under
/// <see cref="PortalConversationGrouping.ConversationsLabel"/>. The switcher's whole purpose is that
/// one query reaches every conversation without the user first knowing where it was filed, so
/// honouring the subtraction would reproduce exactly the findability problem it was built to solve.
/// </para>
/// </remarks>
public static class ConversationSwitcherModel
{
    /// <summary>
    /// Build the switcher view for one agent's conversations under a query.
    /// </summary>
    /// <param name="conversations">The agent's conversations. Enumerated once.</param>
    /// <param name="selectionSource">Current view-selection source, fed to the render projection.</param>
    /// <param name="cronConversationIds">
    /// Authoritative cron-job to conversation-id map, or null. Passed straight through to the
    /// shared grouping helper; a null/empty set degrades to projection-only grouping exactly as it
    /// does for the sidebar and the mobile picker.
    /// </param>
    /// <param name="query">The typed filter. Null, empty or whitespace means "no filter".</param>
    /// <returns>The grouped and flattened view.</returns>
    public static ConversationSwitcherView Build(
        IEnumerable<ConversationState> conversations,
        SelectionSource selectionSource,
        IReadOnlySet<string>? cronConversationIds,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        // Visibility first: PortalListOrdering.IsUserFacingConversation is the ONE predicate for
        // "may the user see this at all". PortalConversationGrouping.ForPicker does not apply it -
        // it partitions whatever it is handed - so omitting it here would let runtime-internal
        // bookkeeping threads (ConversationVisibility.InternalHidden) into the switcher even though
        // the sidebar hides them. Archived rows are dropped for the same reason the cold-start
        // resolver drops them: they are not somewhere the user can switch TO.
        var candidates = conversations
            .Where(PortalListOrdering.IsUserFacingConversation)
            .Where(c => !PortalListOrdering.IsArchivedConversation(c))
            .Where(c => Matches(c, query))
            .ToList();

        if (candidates.Count == 0)
            return ConversationSwitcherView.Empty;

        var groups = PortalConversationGrouping.ForPicker(candidates, selectionSource, cronConversationIds);
        var flattened = groups.SelectMany(g => g.Conversations).ToList();
        return new ConversationSwitcherView(groups, flattened);
    }

    /// <summary>
    /// True when a conversation survives the typed query.
    /// </summary>
    /// <remarks>
    /// Case-insensitive substring over the title, and nothing cleverer. A subsequence or fuzzy match
    /// would rank "Daily Cost Monitor Report" as a hit for "dcm", but it also makes near-miss typing
    /// return a list the user cannot explain, and there is no relevance score here to push the good
    /// hits back to the top. Substring is the behaviour a user can predict from what they typed.
    /// The conversation id is deliberately NOT searched: ids are opaque server-minted tokens, and
    /// matching them would surface rows whose visible title has nothing to do with the query.
    /// </remarks>
    /// <param name="conversation">The conversation to test.</param>
    /// <param name="query">The typed filter; null/empty/whitespace matches everything.</param>
    /// <returns>True when the conversation should be listed.</returns>
    public static bool Matches(ConversationState conversation, string? query)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (string.IsNullOrWhiteSpace(query))
            return true;

        return conversation.Title?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// True when a switcher row leads to a conversation the user can read but not write to, so the
    /// row can say so before it is clicked.
    /// </summary>
    /// <remarks>
    /// Two clauses, because neither alone is complete. <see cref="ConversationRenderProjection.IsUnattended"/>
    /// catches the agent-initiated, sub-agent and ralph rows that group under
    /// <see cref="PortalConversationGrouping.ConversationsLabel"/> alongside ordinary chats and are
    /// otherwise indistinguishable. The group label catches the inverse case: a channel-created
    /// conversation later adopted by a cron job keeps <c>Source = Channel</c> forever (#2304), so
    /// the projection cannot see that it is unattended, and only its membership of the Scheduled
    /// group - which came from the authoritative cron id map - reveals it. Without the second
    /// clause such a row would sit under a "Scheduled" heading with no read-only marker.
    /// <para>
    /// The store's ambient selection source is deliberately NOT threaded in, and
    /// <see cref="ConversationRenderProjection.IsReadOnly"/> is deliberately not the property read.
    /// Both are view state about the conversation currently on screen: while the active view was
    /// promoted by "view sub-agent" they report read-only for EVERY conversation, which would mark
    /// every row in this list read-only for as long as the user observes one. A row must describe
    /// the conversation it points at, not the one being looked at, so a fixed neutral
    /// <see cref="SelectionSource.UserClick"/> is passed and only the selection-independent
    /// <see cref="ConversationRenderProjection.IsUnattended"/> is read.
    /// </para>
    /// </remarks>
    /// <param name="conversation">The conversation the row points at.</param>
    /// <param name="groupLabel">The label of the group the row is rendered under.</param>
    /// <returns>True when the row should carry a read-only marker.</returns>
    public static bool IsReadOnlyRow(ConversationState conversation, string groupLabel)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (conversation.Project(SelectionSource.UserClick).IsUnattended)
            return true;

        return string.Equals(groupLabel, PortalConversationGrouping.ScheduledLabel, StringComparison.Ordinal)
            || string.Equals(groupLabel, PortalConversationGrouping.WebhooksLabel, StringComparison.Ordinal);
    }

}
