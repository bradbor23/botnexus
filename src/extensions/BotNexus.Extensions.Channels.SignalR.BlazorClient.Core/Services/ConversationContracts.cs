using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

#pragma warning disable CS1591 // REST DTOs — public API via JSON deserialization

public sealed record ConversationSummaryDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("activeSessionId")] string? ActiveSessionId,
    [property: JsonPropertyName("bindingCount")] int BindingCount,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("kind")] string Kind = "HumanAgent",
    [property: JsonPropertyName("source")] string Source = "Channel",
    [property: JsonPropertyName("visibility")] string Visibility = "UserFacing",
    [property: JsonPropertyName("isPinned")] bool IsPinned = false,
    [property: JsonPropertyName("pinnedAt")] DateTimeOffset? PinnedAt = null,
    [property: JsonPropertyName("participants")] IReadOnlyList<ParticipantDto>? Participants = null,
    [property: JsonPropertyName("sourceId")] string? SourceId = null,
    [property: JsonPropertyName("purpose")] string? Purpose = null);

public sealed record ParticipantDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string? Role = null);

/// <summary>
/// Wire shape of one row from <c>GET /api/conversations/costs</c> (#2898).
/// </summary>
/// <remarks>
/// The nullable count fields are nullable on the wire too: <c>null</c> means the server did not
/// measure the signal, and it must never be deserialised into or rendered as a measured <c>0</c>
/// (#2554). A legacy server that omits the property entirely lands on the same <c>null</c>, which
/// is the correct reading - it did not measure it either.
/// </remarks>
public sealed record ConversationCostDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("sessionCount")] int SessionCount,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("compactionSummaryCount")] int? CompactionSummaryCount = null,
    [property: JsonPropertyName("totalTokens")] long? TotalTokens = null);
public sealed record CreateConversationRequestDto(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("title")] string? Title);

public sealed record PatchConversationRequestDto(
    [property: JsonPropertyName("title")] string Title);

public sealed record ConversationResponseDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("activeSessionId")] string? ActiveSessionId,
    [property: JsonPropertyName("bindings")] IReadOnlyList<ConversationBindingDto> Bindings,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("modelOverride")] string? ModelOverride = null,
    [property: JsonPropertyName("thinkingOverride")] string? ThinkingOverride = null,
    [property: JsonPropertyName("contextWindowOverride")] int? ContextWindowOverride = null,
    [property: JsonPropertyName("toolOverrideJson")] string? ToolOverrideJson = null,
    [property: JsonPropertyName("kind")] string Kind = "HumanAgent",
    [property: JsonPropertyName("source")] string Source = "Channel",
    [property: JsonPropertyName("visibility")] string Visibility = "UserFacing");

public sealed record SetConversationOverrideRequestDto(
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("thinking")] string? Thinking = null,
    [property: JsonPropertyName("contextWindow")] int? ContextWindow = null,
    [property: JsonPropertyName("toolOverrideJson")] string? ToolOverrideJson = null,
    [property: JsonPropertyName("applyToolOverride")] bool ApplyToolOverride = false);

public sealed record ConversationBindingDto(
    [property: JsonPropertyName("bindingId")] string BindingId,
    [property: JsonPropertyName("channelType")] string ChannelType,
    [property: JsonPropertyName("channelAddress")] string ChannelAddress,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("threadingMode")] string ThreadingMode,
    [property: JsonPropertyName("displayPrefix")] string? DisplayPrefix,
    [property: JsonPropertyName("boundAt")] DateTimeOffset BoundAt);

public sealed record ConversationHistoryResponseDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("entries")] IReadOnlyList<ConversationHistoryEntryDto> Entries);

public sealed class ConversationHistoryEntryDto
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolArgs")]
    public string? ToolArgs { get; init; }

    [JsonPropertyName("toolIsError")]
    public bool ToolIsError { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// True when the entry was folded into a later compaction summary server-side (#2936). Such
    /// entries are still returned so pre-compaction history is reachable, but the portal renders
    /// them collapsed beneath the compaction boundary rather than as ordinary turns. Absent from a
    /// legacy server, which deserialises to <c>false</c> - i.e. the pre-#2936 rendering.
    /// </summary>
    [JsonPropertyName("isFolded")]
    public bool IsFolded { get; init; }

    [JsonPropertyName("thinkingContent")]
    public string? ThinkingContent { get; init; }

    /// <summary>
    /// Orthogonal typed presentation kind of the entry (issue #2149): <c>message</c>,
    /// <c>subagent-completion</c>, or <c>subagent-response</c>. Null/absent from a legacy server
    /// is treated as <c>message</c> by the client.
    /// </summary>
    [JsonPropertyName("messageKind")]
    public string? MessageKind { get; init; }
}

public sealed record SessionHistoryResponseDto(
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("entries")] IReadOnlyList<SessionHistoryEntryDto> Entries);

public sealed class SessionHistoryEntryDto
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolArgs")]
    public string? ToolArgs { get; init; }

    [JsonPropertyName("toolIsError")]
    public bool ToolIsError { get; init; }

    [JsonPropertyName("thinkingContent")]
    public string? ThinkingContent { get; init; }

    /// <summary>
    /// Orthogonal typed presentation kind of the entry (issue #2149): <c>message</c>,
    /// <c>subagent-completion</c>, or <c>subagent-response</c>. Null/absent is treated as
    /// <c>message</c> by the client.
    /// </summary>
    [JsonPropertyName("messageKind")]
    public string? MessageKind { get; init; }
}

/// <summary>
/// One conversation that matched a content search, with the line that proves why.
/// </summary>
/// <remarks>
/// The endpoint deliberately returns ids and snippets rather than whole conversations: the portal
/// already holds the roster with titles, so the client joins on <paramref name="ConversationId"/>
/// instead of the server maintaining a second, divergent conversation projection.
/// </remarks>
/// <param name="ConversationId">The conversation the match was found in.</param>
/// <param name="MatchCount">How many messages in it matched, for ranking and for "3 matches".</param>
/// <param name="Snippet">The best-ranked matching line, already collapsed to one line by the server.</param>
/// <param name="Role">Who said it, so the reader can tell their own words from the agent's.</param>
/// <param name="Timestamp">When it was said.</param>
public sealed record ConversationContentHitDto(
    string ConversationId,
    int MatchCount,
    string? Snippet,
    string? Role,
    DateTimeOffset? Timestamp);

/// <summary>The envelope GET /api/conversations/search returns.</summary>
/// <param name="Query">Echoed back, so a late response can be matched to the query that asked for it.</param>
/// <param name="Results">Matching conversations, best match first.</param>
/// <param name="Count">Number of results.</param>
public sealed record ConversationContentSearchResponseDto(
    string? Query,
    IReadOnlyList<ConversationContentHitDto>? Results,
    int Count);
