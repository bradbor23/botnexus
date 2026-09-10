using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Skills.Recording;

/// <summary>The outcome of checking a proposal against the run it claims to describe.</summary>
/// <param name="Errors">Reasons the proposal must not become a draft. Non-empty means rejected.</param>
/// <param name="Warnings">Observations worth showing an operator that do not block the proposal.</param>
/// <param name="UnparameterisedLiterals">
/// Values that occur in BOTH the recorded run and the proposed body and were left literal. These
/// are the candidates an operator is being asked to rule on at confirm time.
/// </param>
public sealed record DraftValidation(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnparameterisedLiterals)
{
    /// <summary>True when nothing blocks the proposal.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Checks a proposed skill against the trace it was recorded from.
/// </summary>
/// <remarks>
/// <para>
/// This class is the reason recorded skills are worth building at all. Measurement of 1,702 tool
/// calls on this deployment established that a trace cannot identify parameters by itself: only 27%
/// of calls ever repeat verbatim, and with arguments stripped the commonest three-step sequence is
/// <c>bash → bash → bash</c>. All the signal is in the arguments, and the arguments are what vary.
/// </para>
/// <para>
/// So the trace is not asked to identify parameters. The agent proposes them and the operator rules
/// on them, and the trace does the one job it is actually good at: saying whether a claimed value
/// really occurred. That is what keeps the proposal honest without asking it to be derivable.
/// </para>
/// </remarks>
public static class SkillDraftValidator
{
    private static readonly Regex PlaceholderPattern =
        new(@"\{\{\s*([^}\s]+)\s*\}\}", RegexOptions.Compiled);

    private static readonly Regex ValidParameterName =
        new(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>Shortest literal considered when suggesting values that stayed hard-coded.</summary>
    private const int MinLiteralLength = 4;

    /// <summary>
    /// Below this length, "does the value appear in the trace" stops being evidence: a two-character
    /// string occurs by chance in almost any JSON. Such parameters are allowed but flagged, because
    /// silently applying a check that cannot fail is worse than saying it did not apply.
    /// </summary>
    private const int MinCheckableValueLength = 3;

    /// <summary>Cap on the suggestion list, which is meant to be read rather than scrolled.</summary>
    private const int MaxSuggestedLiterals = 15;

    /// <summary>
    /// Validates a proposal. <paramref name="steps"/> must be the LIVE trace, not a persisted copy.
    /// </summary>
    public static DraftValidation Validate(
        string content,
        IReadOnlyList<DraftParameter> parameters,
        IReadOnlyList<RecordedStep> steps)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(steps);

        var errors = new List<string>();
        var warnings = new List<string>();

        var declared = new Dictionary<string, DraftParameter>(StringComparer.Ordinal);
        foreach (var p in parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || !ValidParameterName.IsMatch(p.Name))
            {
                errors.Add(
                    $"Parameter name '{p.Name}' is not usable as a slot. Use lowercase letters, " +
                    "digits, hyphens and underscores, starting with a letter or digit.");
                continue;
            }

            if (!declared.TryAdd(p.Name, p))
            {
                errors.Add($"Parameter '{p.Name}' is declared more than once.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(p.Description))
                errors.Add(
                    $"Parameter '{p.Name}' has no description. The description is what an operator " +
                    "reads when deciding whether this really varies between runs.");

            if (string.IsNullOrEmpty(p.ObservedValue))
            {
                errors.Add(
                    $"Parameter '{p.Name}' has no observed value. Every parameter must record the " +
                    "literal this run actually used, so the proposal can be checked against the trace.");
                continue;
            }

            // A slot declared but never placed replays as whatever the body already says — the
            // parameter looks live in the listing and changes nothing when supplied.
            if (!ContainsPlaceholder(content, p.Name))
                errors.Add(
                    $"Parameter '{p.Name}' is declared but '{{{{{p.Name}}}}}' does not appear in the " +
                    "body, so supplying it would change nothing. Place the slot, or drop the parameter.");

            if (p.ObservedValue.Length < MinCheckableValueLength)
            {
                warnings.Add(
                    $"Parameter '{p.Name}' observed value '{p.ObservedValue}' is too short for the " +
                    "trace check to mean anything — a value this short matches almost any run. " +
                    "It was accepted unverified.");
            }
            else if (!AppearsInTrace(p.ObservedValue, steps))
            {
                errors.Add(
                    $"Parameter '{p.Name}' claims this run used '{Truncate(p.ObservedValue, 80)}', " +
                    "but that value appears in no recorded tool call. A skill must be built from " +
                    "what ran, not from what was remembered.");
            }
        }

        // The mirror check: a slot in the body with no declaration behind it is written into the
        // installed skill verbatim, so a replay reads "{{title}}" as an instruction.
        foreach (Match match in PlaceholderPattern.Matches(content))
        {
            var slot = match.Groups[1].Value;
            if (!declared.ContainsKey(slot))
                errors.Add(
                    $"The body contains '{{{{{slot}}}}}' but no parameter '{slot}' is declared, so a " +
                    "replay would read that placeholder as literal text. Declare it, or remove the slot.");
        }

        if (steps.Count == 0)
            errors.Add(
                "This session recorded no tool calls, so there is nothing to build a skill from. " +
                "A skill proposed here would be prose with no run behind it.");

        return new DraftValidation(
            errors,
            warnings,
            errors.Count > 0 ? [] : FindUnparameterisedLiterals(content, declared.Values, steps));
    }

    /// <summary>
    /// Finds values that really occurred in the run, appear verbatim in the proposed body, and were
    /// left hard-coded. This is the other half of the confirm question: the agent has said what it
    /// thinks varies, and this says what it has decided is fixed, so the operator rules on both.
    /// </summary>
    private static IReadOnlyList<string> FindUnparameterisedLiterals(
        string content,
        IEnumerable<DraftParameter> parameters,
        IReadOnlyList<RecordedStep> steps)
    {
        var parameterised = parameters.Select(p => p.ObservedValue).ToList();
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.ArgumentsJson))
                continue;

            foreach (var literal in ExtractStringLeaves(step.ArgumentsJson))
            {
                if (literal.Length < MinLiteralLength)
                    continue;
                if (!content.Contains(literal, StringComparison.Ordinal))
                    continue;
                // Already spoken for: either it IS a parameter's value, or it sits inside one.
                if (parameterised.Any(v => v.Contains(literal, StringComparison.Ordinal)))
                    continue;

                found.Add(literal);
            }
        }

        return found
            .OrderByDescending(v => v.Length)
            .ThenBy(v => v, StringComparer.Ordinal)
            .Take(MaxSuggestedLiterals)
            .Select(v => Truncate(v, 120))
            .ToList();
    }

    /// <summary>
    /// Walks a tool call's JSON arguments and yields every string value, at any depth. Property
    /// NAMES are skipped: an argument called "command" is part of the tool's contract, not a value
    /// this run chose, so offering it as a candidate parameter would be noise.
    /// </summary>
    private static IEnumerable<string> ExtractStringLeaves(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Arguments that are not valid JSON are still a real record of a call; they simply
            // yield no structured candidates. Dropping the step entirely would be worse.
            yield break;
        }

        using (document)
        {
            foreach (var value in Walk(document.RootElement))
                yield return value;
        }
    }

    private static IEnumerable<string> Walk(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    foreach (var value in Walk(property.Value))
                        yield return value;
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var value in Walk(item))
                        yield return value;
                break;
        }
    }

    /// <summary>
    /// True when the value occurs in some recorded call's arguments.
    /// </summary>
    /// <remarks>
    /// Checked against BOTH the raw argument text and its JSON-escaped form. A value containing a
    /// quote, a backslash or a newline — a Windows path, a shell command with an embedded string —
    /// is stored escaped, so a raw comparison alone would reject perfectly honest proposals for
    /// exactly the values most worth parameterising.
    /// </remarks>
    private static bool AppearsInTrace(string value, IReadOnlyList<RecordedStep> steps)
    {
        var escaped = JsonSerializer.Serialize(value);
        escaped = escaped.Length >= 2 ? escaped[1..^1] : escaped;

        foreach (var step in steps)
        {
            if (string.IsNullOrEmpty(step.ArgumentsJson))
                continue;
            if (step.ArgumentsJson.Contains(value, StringComparison.Ordinal))
                return true;
            if (step.ArgumentsJson.Contains(escaped, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>True when <c>{{name}}</c> appears in the body, tolerating inner whitespace.</summary>
    public static bool ContainsPlaceholder(string content, string name)
    {
        foreach (Match match in PlaceholderPattern.Matches(content))
        {
            if (string.Equals(match.Groups[1].Value, name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Replaces every declared slot in <paramref name="content"/> with its supplied value.</summary>
    public static string Substitute(string content, IReadOnlyDictionary<string, string> values)
        => PlaceholderPattern.Replace(content, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

    /// <summary>Lists the distinct slot names appearing in a body, in first-seen order.</summary>
    public static IReadOnlyList<string> PlaceholdersIn(string content)
    {
        var seen = new List<string>();
        foreach (Match match in PlaceholderPattern.Matches(content))
        {
            var slot = match.Groups[1].Value;
            if (!seen.Contains(slot, StringComparer.Ordinal))
                seen.Add(slot);
        }

        return seen;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
