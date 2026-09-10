using System.Globalization;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences test observation windows against wall-clock assumptions (#2825).
/// </summary>
/// <remarks>
/// <para>
/// A test that only passes on an idle machine is a broken test. Production runs under CPU
/// contention too, so a suite that fails when the host is busy cannot distinguish a code bug
/// from a test bug - which is the whole point of having it.
/// </para>
/// <para>
/// Measured on 2026-08-06: eight identical container runs of one commit produced a consistent
/// 6/8 pass rate, and every failure was a different test asserting a short fixed duration.
/// Failures occurred only in the slow (~14 min) lanes, never the fast (~11 min) ones.
/// </para>
/// <para>
/// This fences OBSERVATION windows only - "wait until X becomes true". Widening those cannot
/// weaken an assertion, because the condition must still be met. A duration that is the
/// SUBJECT under test (a product timeout the test asserts fires) is deliberately not covered:
/// there a short value is the point.
/// </para>
/// <para>
/// <b>The rule, in one sentence.</b> If the deadline is reached on the FAILING path it must be
/// generous, because reaching it means something is already wrong and the only question is how
/// legibly that gets reported. If it is reached on the PASSING path it is the assertion, must
/// stay short, and must say so at the call site with a <c>deadline-is-the-assertion:</c> comment.
/// </para>
/// <para>
/// <b>Why <c>WaitAsync</c> is fenced too (#107).</b> <see cref="TestDelayFlakeFenceTests"/> moved
/// authors off <c>Task.Delay</c> and onto <c>task.WaitAsync(TimeSpan)</c>, and this fence's helper
/// list did not cover it - so a hand-written five-second deadline satisfied every fence in the repo
/// and still failed on a loaded runner. It did exactly that three times: <c>TelegramMultiBotTests</c>
/// on PR #75, <c>InboundBoundaryObservabilityTests</c> on <c>main</c> at 6c215e2c, and
/// <c>FileWatcherToolTests</c> on PR #103 - each on a diff that could not reach the code involved.
/// Two of the three fences pointing somewhere and the third not following is how a flake fence ends
/// up steering people into the flake it exists to prevent.
/// </para>
/// <para>
/// <b>Why tool-argument budgets are NOT regex-fenced.</b> <c>["timeout"] = 5</c> passed to a tool
/// under test is the same wall-clock deadline spelled as data, and it is what took
/// <c>FileWatcherToolTests</c> red. It is nevertheless left to review rather than to a scanner,
/// because a scanner cannot tell the two meanings apart: of the 62 such literals in this suite the
/// large majority - <c>ConfigHydrationServiceTests</c>, <c>AgentConverseToolTests</c>' coercion
/// cases, <c>ShellToolTimeoutCeilingTests</c> - are numbers the test PARSES or CLAMPS and never
/// waits out, and fencing those would be almost entirely false positives. The distinction is
/// semantic, so the guidance is documentary: a tool argument the test spends is subject to the same
/// rule as a deadline, and should be named (see <c>FileWatcherToolTests.WatchBudgetSeconds</c>)
/// rather than written as a bare literal.
/// </para>
/// </remarks>
public class TestObservationWindowTests : ArchitectureTest
{
    /// <summary>
    /// Helpers whose <c>TimeSpan</c> argument is an observation budget. These carry no baseline: a
    /// new short window here fails outright.
    /// </summary>
    private static readonly string[] WaitHelpers =
    [
        "WaitUntilAsync", "WaitForAsync", "WaitForOutboundAsync", "WaitForConditionAsync",
        "EventuallyAsync", "WaitForStatusAsync", "PollUntilAsync", "SignaledAsync"
    ];

    /// <summary>
    /// The raw deadline form, fenced since #107 and baselined: 107 call sites predate the rule.
    /// </summary>
    private static readonly string[] RawDeadlineForms = ["WaitAsync"];

    private const int MinimumObservationSeconds = 15;

    private const string BaselineFileName = "TestObservationWindowBaseline.baseline";

    // Both counts are shrink-only. Lower them when a deadline is made generous or replaced by a
    // signal; never raise them to admit a new one.
    // #111 ratchet: TelegramChannelAdapterTests' 19 five-second deadlines all waited on a signal the
    // stub handler or the mock dispatcher already raised, so every one became a SignaledAsync call
    // and the file left the baseline entirely.
    // #111 ratchet: every remaining five-second deadline - the value behind all three failures this
    // fence was built for (#75, #103, main at 6c215e2c). All 29 were the same case: a wait on a signal
    // the fixture already raises. Nothing at 5s or below survives in the baseline.
    // #111 ratchet: the eight sub-five-second deadlines, the shortest in the baseline. Seven waited
    // on a signal and became SignaledAsync calls. The eighth - ConversationLostUpdateSeamTests'
    // 200ms SeamGate wait - is the first site to claim the justification marker: its expiry IS the
    // assertion, but it reports the condition as SeamDeadlockException, which the automatic
    // TimeoutException exemption cannot see.
    private const int ExpectedBaselineEntryCount = 32;
    private const int ExpectedBaselineViolationCount = 107;

    /// <summary>
    /// Rejects short observation windows on the shared polling helpers, which carry no legacy debt.
    /// </summary>
    [Fact]
    public void ObservationWindows_AreGenerousEnoughForALoadedHost()
    {
        var violations = Scan(WaitHelpers)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value.Select(site => $"{pair.Key}:{site.Line} waits only {site.Seconds:0.##}s"))
            .ToList();

        violations.ShouldBeEmpty(
            $"Observation windows must tolerate a loaded host (>= {MinimumObservationSeconds}s). " +
            "Poll for the condition instead of assuming the machine is fast; widening an " +
            "observation window cannot weaken an assertion because the condition must still " +
            $"be met.{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    /// Rejects hand-written <c>WaitAsync</c> deadlines beyond the frozen debt.
    /// </summary>
    [Fact]
    public void RawDeadlines_IntroduceNoNewShortObservationWindows()
    {
        var baseline = ReadBaseline();
        var actual = Scan(RawDeadlineForms);
        var offenders = new List<string>();

        foreach (var (path, sites) in actual.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var allowed = baseline.TryGetValue(path, out var count) ? count : 0;
            if (sites.Count <= allowed)
                continue;

            // Every site is listed, not just the ones past the allowance. The baseline records a
            // COUNT, not which lines it forgave, so Skip(allowed) names whichever sites happen to
            // fall last in file order - which is rarely the one just added. Reporting a line the
            // author did not touch sends them to the wrong place; listing all of them, and saying
            // how many are pre-existing, sends them to the right one.
            offenders.Add(
                $"{path}: {sites.Count} short deadline(s), baseline allows {allowed}. " +
                $"All {sites.Count} in this file (the baseline does not record which {allowed} it " +
                "forgives, so look for the one you added): " +
                string.Join("; ", sites.Select(site => $"L{site.Line} waits only {site.Seconds:0.##}s")));
        }

        offenders.ShouldBeEmpty(
            "A wait that is bounded by the clock rather than by a signal fails whenever CI is busy. " +
            "The signal is the synchronisation; the deadline only decides how a hang gets reported, " +
            "so it is never reached on the passing path and there is nothing to buy by keeping it " +
            $"tight. Await the fixture's own signal through TestAwait.SignaledAsync, or give the " +
            $"deadline at least {MinimumObservationSeconds}s. If its EXPIRY is what you are asserting, " +
            $"say so with a '{ShortDeadlineScanner.JustificationMarker} <reason>' comment on the " +
            "line or just above it. " +
            "Do not add entries to the baseline." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Forces the baseline to ratchet downward whenever a short deadline is removed.
    /// </summary>
    [Fact]
    public void RawDeadlineBaseline_HasNoStaleEntries()
    {
        var baseline = ReadBaseline();
        var actual = Scan(RawDeadlineForms);
        var stale = new List<string>();

        baseline.Count.ShouldBe(
            ExpectedBaselineEntryCount,
            "The short-deadline baseline file count may only shrink; lower the expected count when removing an entry.");
        baseline.Values.Sum().ShouldBe(
            ExpectedBaselineViolationCount,
            "The short-deadline baseline count may only shrink; lower the expected count when removing a deadline.");

        foreach (var (path, allowed) in baseline.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var count = actual.TryGetValue(path, out var sites) ? sites.Count : 0;
            if (count < allowed)
                stale.Add($"{path}: baseline allows {allowed} but only {count} remain.");
        }

        stale.ShouldBeEmpty(
            "The short-deadline baseline is shrink-only. Lower or remove an entry whenever a deadline " +
            "is made generous or replaced by a signal." +
            Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>Pins the boundary between a deadline, an exempt one, and prose that merely shows one.</summary>
    [Theory]
    [InlineData("await ready.WaitAsync(TimeSpan.FromSeconds(5));", true)]
    [InlineData("await ready.WaitAsync(TimeSpan.FromMilliseconds(200));", true)]
    [InlineData("await ready.WaitAsync(TimeSpan.FromSeconds(30));", false)]
    [InlineData("await ready.WaitAsync(TimeSpan.FromSeconds(15));", false)]
    [InlineData("await ready.WaitAsync(cancellationToken);", false)]
    // A deadline shown in prose is documentation, not a deadline.
    [InlineData("// prefer this over ready.WaitAsync(TimeSpan.FromSeconds(5));", false)]
    // Expiry as the assertion, claimed either by the existing heuristic or by the marker.
    [InlineData("await Should.ThrowAsync<TimeoutException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(2)));", false)]
    [InlineData("await run.WaitAsync(TimeSpan.FromSeconds(2)); // deadline-is-the-assertion: expiry is the pass", false)]
    public void DeadlineClassifier_DistinguishesBudgetsFromAssertionsAndProse(string source, bool expectedViolation)
    {
        ShortDeadlineScanner
            .FindViolations(source, RawDeadlineForms, MinimumObservationSeconds)
            .Any()
            .ShouldBe(expectedViolation);
    }

    /// <summary>Proves the justification marker must carry a reason rather than stand alone.</summary>
    [Fact]
    public void DeadlineClassifier_RequiresAReasonAfterTheMarker()
    {
        ShortDeadlineScanner
            .FindViolations(
                "await run.WaitAsync(TimeSpan.FromSeconds(2)); // deadline-is-the-assertion:",
                RawDeadlineForms,
                MinimumObservationSeconds)
            .ShouldNotBeEmpty("a bare marker is a claim without a reason, so it does not exempt the deadline");
    }

    private Dictionary<string, List<ShortDeadlineScanner.Violation>> Scan(IReadOnlyCollection<string> helpers)
    {
        var result = new Dictionary<string, List<ShortDeadlineScanner.Violation>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Repository.TestsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(nameof(TestObservationWindowTests) + ".cs", StringComparison.Ordinal))
            {
                continue;
            }

            var violations = ShortDeadlineScanner.FindViolations(
                File.ReadAllText(file), helpers, MinimumObservationSeconds);
            if (violations.Count == 0)
                continue;

            var relativePath = Path.GetRelativePath(Repository.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            result.Add(relativePath, violations);
        }

        return result;
    }

    private static Dictionary<string, int> ReadBaseline() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, BaselineFileName))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('|', 2))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);

}

/// <summary>
/// Finds wall-clock deadlines shorter than the minimum a loaded CI host can be relied on to meet.
/// </summary>
internal static class ShortDeadlineScanner
{
    /// <summary>
    /// Marks a deadline whose EXPIRY is the assertion, so a short value is correct. Must appear on
    /// the deadline's own line or within <see cref="JustificationLookbackLines"/> lines above it,
    /// followed by the reason.
    /// </summary>
    internal const string JustificationMarker = "deadline-is-the-assertion:";

    private const int JustificationLookbackLines = 10;

    private static readonly Dictionary<string, Regex> Patterns = [];

    /// <summary>A deadline that is too short, and the budget it actually allows.</summary>
    internal sealed record Violation(int Line, double Seconds);

    internal static List<Violation> FindViolations(
        string source,
        IReadOnlyCollection<string> helpers,
        int minimumSeconds)
    {
        var violations = new List<Violation>();
        var lines = source.Split('\n');
        var masked = MaskLineComments(lines);
        var pattern = PatternFor(helpers);

        foreach (Match match in pattern.Matches(masked))
        {
            var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            var seconds = match.Groups["unit"].Value == "Seconds" ? value : value / 1000d;
            if (seconds >= minimumSeconds)
                continue;

            // A test that asserts a timeout is THROWN needs a short window by design.
            var context = masked[
                Math.Max(0, match.Index - 200)..
                Math.Min(masked.Length, match.Index + match.Length + 300)];
            if (context.Contains("ThrowAsync<TimeoutException>", StringComparison.Ordinal))
                continue;

            // A poll INTERVAL is not an observation budget - a tight interval makes the wait more
            // responsive, not less tolerant, so it is correct as written. The helper's own
            // DECLARATION matches this pattern too (its default interval is a parameter default,
            // not a budget), so skip method definitions as well.
            var argument = masked[match.Index..(match.Index + match.Length)];
            if (argument.Contains("pollInterval", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("interval:", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("Func<", StringComparison.Ordinal)
                || argument.Contains("TimeSpan timeout", StringComparison.Ordinal))
            {
                continue;
            }

            var line = masked[..match.Index].Count(character => character == '\n') + 1;
            if (IsJustified(lines, line))
                continue;

            violations.Add(new Violation(line, seconds));
        }

        return violations;
    }

    /// <summary>
    /// Compiles one regex per helper set. <see cref="FindViolations"/> runs over every test source
    /// for each fenced form, so rebuilding the pattern per file costs thousands of compilations.
    /// </summary>
    private static Regex PatternFor(IReadOnlyCollection<string> helpers)
    {
        var key = string.Join('|', helpers);
        lock (Patterns)
        {
            if (Patterns.TryGetValue(key, out var cached))
                return cached;

            var pattern = new Regex(
                $@"(?:{key})\s*\([^;]*?TimeSpan\.From(?<unit>Seconds|Milliseconds)\(\s*(?<value>\d+(?:\.\d+)?)\s*\)",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            Patterns[key] = pattern;
            return pattern;
        }
    }

    /// <summary>
    /// Reports whether the deadline claims, with a reason, that its expiry is the assertion. The
    /// claim is read from the ORIGINAL lines rather than the masked ones, because it lives in a
    /// comment by construction.
    /// </summary>
    private static bool IsJustified(string[] lines, int line)
    {
        var first = Math.Max(0, line - 1 - JustificationLookbackLines);
        for (var index = first; index < line && index < lines.Length; index++)
        {
            var marker = lines[index].IndexOf(JustificationMarker, StringComparison.Ordinal);
            if (marker < 0)
                continue;

            var reason = lines[index][(marker + JustificationMarker.Length)..];
            if (!string.IsNullOrWhiteSpace(reason))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Blanks out line comments while preserving every offset, so a deadline WRITTEN ABOUT in prose
    /// is not read as one while line numbers and context windows stay exact.
    /// </summary>
    private static string MaskLineComments(string[] lines)
    {
        var masked = new string[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var comment = lines[index].IndexOf("//", StringComparison.Ordinal);
            masked[index] = comment < 0
                ? lines[index]
                : lines[index][..comment] + new string(' ', lines[index].Length - comment);
        }

        return string.Join('\n', masked);
    }
}
