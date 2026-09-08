namespace BotNexus.Agent.Providers.Core;

/// <summary>
/// Splits a system prompt into the part that is stable across requests and the part that is not,
/// and relocates the unstable part to where it cannot cost anything.
/// </summary>
/// <remarks>
/// <para>
/// Every provider builds its cache prefix in the same order: tools, then system, then messages.
/// The system prompt therefore sits in front of the entire conversation, and anything volatile
/// inside it invalidates not just itself but every message behind it. Splitting the prompt into a
/// cached half and an uncached half -- which is what the boundary marker already did -- protects
/// the stable half of the prompt and nothing else: the moment a watched file changes, the whole
/// history re-bills at the uncached rate.
/// </para>
/// <para>
/// The fix is to move the volatile half out of the system prompt entirely and append it to the end
/// of the conversation, behind the last cache breakpoint. The stable prefix then grows from
/// "tools plus half a system prompt" to "tools plus system prompt plus every prior turn", and the
/// volatile text rides in the one place that was never going to be cached anyway.
/// </para>
/// <para>
/// The ordering this depends on is easy to get backwards. Appended <em>before</em> the breakpoint,
/// the volatile text becomes part of the cached prefix -- and because it is rebuilt per request and
/// never persisted, the next request's prefix would no longer match what was cached. That turns a
/// fix into a guaranteed miss on every turn. It must be appended after breakpoints are placed.
/// </para>
/// </remarks>
public static class SystemPromptPartition
{
    /// <summary>
    /// Marker the gateway writes into the system prompt to separate the stable prefix from the
    /// frequently-changing tail.
    /// </summary>
    public const string BoundaryMarker = "\n<!-- BOTNEXUS_CACHE_BOUNDARY -->\n";

    /// <summary>
    /// Wrapper placed around relocated content. It is being moved out of the system prompt into a
    /// user-role turn, so it has to say plainly what it is: the model must not read a watched
    /// file's contents as something the user just said.
    /// </summary>
    private const string OpenTag =
        "<system_context note=\"Supplied by BotNexus, not written by the user.\">";

    private const string CloseTag = "</system_context>";

    /// <summary>
    /// Splits a system prompt at <see cref="BoundaryMarker"/>.
    /// </summary>
    /// <param name="systemPrompt">The full system prompt.</param>
    /// <returns>
    /// The stable text, and the volatile tail when there is one. A prompt with no marker is
    /// entirely stable. A marker with nothing usable on either side falls back to treating the
    /// whole prompt as stable rather than producing two empty halves.
    /// </returns>
    public static (string Stable, string? Volatile) Split(string systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);

        var markerIndex = systemPrompt.IndexOf(BoundaryMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return (systemPrompt, null);

        var stable = systemPrompt[..markerIndex].TrimEnd();
        var volatileTail = systemPrompt[(markerIndex + BoundaryMarker.Length)..].TrimStart();

        if (string.IsNullOrWhiteSpace(stable) && string.IsNullOrWhiteSpace(volatileTail))
            return (systemPrompt, null);

        return (stable, string.IsNullOrWhiteSpace(volatileTail) ? null : volatileTail);
    }

    /// <summary>
    /// Appends relocated context to the end of the last message, as a trailing text block.
    /// </summary>
    /// <param name="messages">The converted message list, already carrying its breakpoints.</param>
    /// <param name="volatileText">The text to relocate.</param>
    /// <returns>
    /// <c>true</c> when the text was appended. <c>false</c> means the caller must keep it in the
    /// system prompt: there is nowhere safe to put it, and dropping context is far worse than
    /// paying for it.
    /// </returns>
    /// <remarks>
    /// Only a user-role message is a valid destination. Appending to an assistant turn would put
    /// words in the model's own mouth, and appending to nothing at all would lose the context, so
    /// both cases decline and let the caller fall back.
    /// </remarks>
    public static bool TryAppendToConversation(
        List<Dictionary<string, object?>> messages, string? volatileText)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (string.IsNullOrWhiteSpace(volatileText) || messages.Count == 0)
            return false;

        var last = messages[^1];
        if (last.TryGetValue("role", out var role) &&
            !string.Equals(role as string, "user", StringComparison.Ordinal))
        {
            return false;
        }

        var block = new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = Wrap(volatileText)
        };

        switch (last["content"])
        {
            case List<object> blocks:
                blocks.Add(block);
                return true;

            case string existing:
                last["content"] = new List<object>
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = existing },
                    block
                };
                return true;

            default:
                // Unrecognised content shape. Declining costs a cache prefix; guessing could drop
                // the message's real content.
                return false;
        }
    }

    /// <summary>Wraps relocated content so it reads as supplied context rather than user speech.</summary>
    public static string Wrap(string volatileText) =>
        $"{OpenTag}\n{volatileText}\n{CloseTag}";
}
