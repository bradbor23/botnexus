using System.Reflection;
using BotNexus.Testing;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// The wait-helper names the timing fences recognise, in the two groupings they actually mean.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the lists drifting is not hypothetical - it is the defect that started #107.
/// <see cref="TestDelayFlakeFenceTests"/> banned <c>Task.Delay</c>, authors moved to
/// <c>WaitAsync</c>, and <see cref="TestObservationWindowTests"/>' private copy of the list did not
/// include it, so a five-second deadline satisfied every fence in the repo and still failed on a
/// loaded runner. It recurred in miniature when <c>SettledAsync</c> was added in #126 and went
/// unfenced.
/// </para>
/// <para>
/// The two fences do NOT want the same list, which is why merging them into one was wrong and is
/// worth recording. A helper that POLLS a condition tells you only that the condition flipped - it
/// does not order a mock call the same background continuation makes afterwards, which is the race
/// <see cref="TestConcurrencyFlakeFenceTests"/> exists to catch. A helper that AWAITS a task tells
/// you the work itself finished, which is a genuine happens-before edge and must not be reported as
/// that race. Both kinds carry a deadline, so both matter to
/// <see cref="TestObservationWindowTests"/>.
/// </para>
/// </remarks>
internal static class TestWaitHelperNames
{
    /// <summary>
    /// Helpers whose completion means only "a condition became true". Their completion does not
    /// order work the code under test performs later.
    /// </summary>
    internal static readonly string[] PollingHelpers =
    [
        // Project-local pollers that predate TestAwait and are still called in places.
        "WaitUntilAsync", "WaitForAsync", "WaitForOutboundAsync", "WaitForConditionAsync",
        "WaitForStatusAsync", "PollUntilAsync",
        // TestAwait's polling surface.
        "EventuallyAsync", "TryEventuallyAsync"
    ];

    /// <summary>
    /// Every form whose <c>TimeSpan</c> argument is an observation budget: the pollers above, plus
    /// the helpers that await a task directly, plus the raw framework form.
    /// </summary>
    internal static readonly string[] BudgetedWaits =
    [
        .. PollingHelpers,
        // Direct awaits: these DO order later work, so they belong here and not above.
        "SignaledAsync", "SettledAsync",
        // The raw framework form. Fenced since #107; it is what authors reach for by default.
        "WaitAsync"
    ];
}

/// <summary>
/// Fails when <see cref="TestAwait"/> grows a helper the fences do not know about, or one they
/// would classify wrongly.
/// </summary>
/// <remarks>
/// A fence that enumerates what it catches is only as good as that enumeration, and one maintained
/// by hand falls behind the moment a helper is added - which is how <c>SettledAsync</c> shipped
/// unfenced. Membership is derived from each helper's own signature rather than its name, so a new
/// helper is classified by what it does: a first parameter of <c>Func&lt;…&gt;</c> is a poller, a
/// first parameter of <c>Task</c> is a direct await.
/// </remarks>
public class TestWaitHelperCoverageTests
{
    private static IEnumerable<MethodInfo> PublicHelpers() =>
        typeof(TestAwait)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal));

    [Fact]
    public void EveryPublicHelper_CarriesAnObservationBudgetTheFencesCanSee()
    {
        var uncovered = PublicHelpers()
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !TestWaitHelperNames.BudgetedWaits.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        uncovered.ShouldBeEmpty(
            "TestAwait gained a helper the timing fences do not recognise, so a short deadline passed " +
            "to it would not be caught. Add it to TestWaitHelperNames.BudgetedWaits. This is the same " +
            "drift that let WaitAsync go unfenced until #107: " + string.Join(", ", uncovered));
    }

    [Fact]
    public void PollingHelpers_AreExactlyTheOnesThatPollAConditionRatherThanAwaitWork()
    {
        var misclassified = new List<string>();

        foreach (var method in PublicHelpers())
        {
            var first = method.GetParameters().FirstOrDefault()?.ParameterType;
            if (first is null)
                continue;

            var polls = first.IsGenericType && first.GetGenericTypeDefinition().Name.StartsWith("Func", StringComparison.Ordinal);
            var listed = TestWaitHelperNames.PollingHelpers.Contains(method.Name, StringComparer.Ordinal);

            if (polls && !listed)
                misclassified.Add($"{method.Name} polls a condition but is not in PollingHelpers");
            else if (!polls && listed)
                misclassified.Add($"{method.Name} awaits work but is listed as a poller");
        }

        misclassified.Distinct(StringComparer.Ordinal).ShouldBeEmpty(
            "A poller's completion does not order work the code under test does afterwards; a direct " +
            "await's does. Getting this backwards either misses the verify-after-poll race or reports " +
            "it against tests that are correctly ordered - which is exactly what happened when both " +
            "fences were briefly given the same list." + Environment.NewLine +
            string.Join(Environment.NewLine, misclassified));
    }
}
