using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Prevents tests from synchronising through finite wall-clock sleeps instead of observable signals.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacing a sleep with <c>WaitAsync(TimeSpan)</c> does not satisfy this fence's intent.</b> It
/// is still a finite wall-clock deadline and still fails when CI is saturated - the ban simply moves
/// the flake somewhere this scanner cannot see it. That happened, repeatedly: a comment in
/// <c>InboundBoundaryObservabilityTests</c> read "Task.Delay is banned in tests by
/// TestDelayFlakeFenceTests; WaitAsync is the sanctioned form", and hand-written five-second
/// deadlines then took three unrelated PRs red (#75, #103, and <c>main</c> at 6c215e2c). A fence that
/// names what is forbidden without naming what is correct redirects the defect rather than removing
/// it.
/// </para>
/// <para>
/// What is actually sanctioned, in order of preference: await a signal the fixture raises through
/// <c>TestAwait.SignaledAsync</c>; poll for the observable condition through
/// <c>TestAwait.EventuallyAsync</c>; or drive the clock yourself with <c>ManualTimeProvider</c>. All
/// three end when the thing you are waiting for happens, not when a guess about the host's speed
/// expires. <see cref="TestObservationWindowTests"/> enforces the deadline half of that contract,
/// <c>WaitAsync</c> included.
/// </para>
/// <para>
/// <b>Not every finite wait is a guess (#111 follow-up).</b> When the baseline was read site by site,
/// roughly two thirds of its 145 entries turned out to be correct code the scanner cannot tell apart
/// from debt: a fake that is slow ON PURPOSE (<c>DelayingAction</c>, <c>DelayedHandler</c>, a stalling
/// stream), a backoff against a genuinely external resource (temp-dir cleanup, an <c>IOException</c>
/// retry, a TCP readiness probe, rate-limit spacing against a live API), or the delay the test is
/// actually about. Freezing those as debt made the count meaningless - it could not distinguish
/// "a test that guesses" from "a test that simulates". A wait that claims
/// <c>delay-is-not-a-signal:</c> with a reason is therefore exempt, the same way the deadline fence
/// exempts a wait whose expiry is the assertion. The claim is the point: it makes the author say
/// which it is.
/// </para>
/// <para>
/// The 145 waits that predated the rule were retired over #123, #126, #129, #130 and #131, and the
/// shrink-only baseline was DELETED rather than left at 0/0 - which makes the rule stronger than it
/// was, because a finite wait now fails outright instead of being measured against an allowance. Do
/// not reintroduce a baseline to admit one; use a helper, or the marker if the delay genuinely is
/// not standing in for a signal.
/// </para>
/// </remarks>
public class TestDelayFlakeFenceTests : ArchitectureTest
{
    private static readonly Regex LocalPollerDeclaration = new(
        @"\b(?:private|protected|internal|public)\s+(?:static\s+)?(?:async\s+)?Task\s+" +
        @"(?:WaitUntilAsync|WaitForAsync|EventuallyAsync|PollUntilAsync)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Pins the lexical boundary so cancellation sentinels remain valid while finite sleeps are caught.
    /// </summary>
    [Theory]
    [InlineData("await Task.Delay(20);", true)]
    [InlineData("await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);", true)]
    [InlineData("Thread.Sleep(100);", true)]
    [InlineData("await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);", false)]
    [InlineData("await Task.Delay(Timeout.Infinite, cancellationToken);", false)]
    [InlineData("await Task.Delay(\n    Timeout.InfiniteTimeSpan,\n    cancellationToken);", false)]
    [InlineData("// await Task.Delay(20);", false)]
    // A wait written inside a STRING is prose about a delay, not a delay.
    [InlineData("throw new Exception(\"If you needed Task.Delay(..) here the binding is lazy\");", false)]
    // A "//" inside a string must not truncate the line and hide the real wait after it.
    [InlineData("Log(\"see https://x/y\"); await Task.Delay(20);", true)]
    // Claimed, with a reason, as something other than a stand-in for a signal.
    [InlineData("await Task.Delay(_gap, ct); // delay-is-not-a-signal: the stall IS the subject", false)]
    // A bare claim with no reason does not exempt anything.
    [InlineData("await Task.Delay(_gap, ct); // delay-is-not-a-signal:", true)]
    public void FiniteWaitClassifier_DistinguishesSleepsFromCancellationSentinels(
        string source,
        bool expectedViolation)
    {
        FiniteTestDelayScanner.FindViolations(source).Any().ShouldBe(expectedViolation);
    }

    /// <summary>
    /// Rejects every finite wall-clock wait. There is no allowance and no baseline: with the
    /// pre-existing debt retired, one of these is a defect rather than history.
    /// </summary>
    [Fact]
    public void Tests_IntroduceNoFiniteWallClockWaits()
    {
        var offenders = ScanTestSources()
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
                $"{pair.Key}: " +
                string.Join("; ", pair.Value.Select(site => $"L{site.Line} {site.Text}")))
            .ToList();

        offenders.ShouldBeEmpty(
            "Tests must use TestAwait.EventuallyAsync to observe a condition, TestAwait.SignaledAsync to " +
            "await a signal the fixture raises, TestAwait.SettledAsync to wait for work to terminate, " +
            "use virtual time, or inject the delay under test instead of sleeping for a finite " +
            "wall-clock duration. Infinite delays that end through cancellation are sentinels and " +
            "remain valid. Rewriting the sleep as WaitAsync(TimeSpan.FromSeconds(n)) does NOT satisfy " +
            "this rule: it is the same wall-clock deadline, it fails on the same loaded runner, and " +
            "TestObservationWindowTests fences it. If the delay is NOT standing in for a signal - it " +
            "is the behaviour being simulated, a backoff against a genuinely external resource, a " +
            "LOWER bound the clock must really cross, or the subject under test - say so with a " +
            $"'{FiniteTestDelayScanner.JustificationMarker} <reason>' comment on the line or just " +
            "above it, and it is exempt." + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Prevents test projects from recreating polling loops with inconsistent timing and diagnostics.
    /// </summary>
    [Fact]
    public void Tests_DoNotDeclareProjectLocalGenericPollers()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateTestSources())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in LocalPollerDeclaration.Matches(text))
            {
                var line = text[..match.Index].Count(character => character == '\n') + 1;
                violations.Add($"{Path.GetRelativePath(Repository.TestsRoot, file)}:{line}");
            }
        }

        violations.ShouldBeEmpty(
            "Generic condition polling belongs in TestAwait.EventuallyAsync so timeout, cancellation, " +
            "poll interval, and diagnostics stay consistent across test projects. Local pollers:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Proves the local-poller predicate matches declarations but not shared-helper calls.</summary>
    [Fact]
    public void LocalPollerClassifier_DistinguishesDeclarationsFromCalls()
    {
        LocalPollerDeclaration.IsMatch(
            "private static async Task WaitUntilAsync(Func<bool> condition) { await Task.Yield(); }")
            .ShouldBeTrue();
        LocalPollerDeclaration.IsMatch(
            "await TestAwait.EventuallyAsync(() => ready, \"the service to be ready\");")
            .ShouldBeFalse();
    }

    private Dictionary<string, List<FiniteTestDelayScanner.Violation>> ScanTestSources()
    {
        var result = new Dictionary<string, List<FiniteTestDelayScanner.Violation>>(StringComparer.Ordinal);

        foreach (var file in EnumerateTestSources())
        {
            var violations = FiniteTestDelayScanner.FindViolations(File.ReadAllText(file));
            if (violations.Count == 0)
                continue;

            var relativePath = Path.GetRelativePath(Repository.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            result.Add(relativePath, violations);
        }

        return result;
    }

    private IEnumerable<string> EnumerateTestSources()
    {
        foreach (var file in Directory.EnumerateFiles(Repository.TestsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith($"{Path.DirectorySeparatorChar}BotNexus.Testing{Path.DirectorySeparatorChar}TestAwait.cs", StringComparison.Ordinal)
                || file.EndsWith(nameof(TestDelayFlakeFenceTests) + ".cs", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

}

internal static partial class FiniteTestDelayScanner
{
    private static readonly Regex FiniteWait = CreateFiniteWaitRegex();

    internal sealed record Violation(int Line, string Text);

    /// <summary>
    /// Marks a finite wait that is NOT standing in for a signal - the delay is the behaviour being
    /// simulated, a backoff against a genuinely external resource, or the subject under test. Must
    /// appear on the wait's own line or within <see cref="JustificationLookbackLines"/> lines above
    /// it, followed by a reason saying which of those it is.
    /// </summary>
    internal const string JustificationMarker = "delay-is-not-a-signal:";

    private const int JustificationLookbackLines = 10;

    internal static List<Violation> FindViolations(string source)
    {
        var violations = new List<Violation>();
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var sourceOffset = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var code = MaskStringsAndComments(lines[index]);
            foreach (Match match in FiniteWait.Matches(code))
            {
                var invocationStart = sourceOffset + match.Index;
                var invocationEnd = normalized.IndexOf(';', invocationStart);
                var invocation = normalized[invocationStart..(invocationEnd < 0 ? normalized.Length : invocationEnd)];
                if (invocation.Contains("Timeout.Infinite", StringComparison.Ordinal))
                    continue;

                if (IsJustified(lines, index + 1))
                    continue;

                violations.Add(new Violation(index + 1, lines[index].Trim()));
            }

            sourceOffset += lines[index].Length + 1;
        }

        return violations;
    }

    /// <summary>
    /// Reports whether the wait claims, with a reason, that it is not standing in for a signal. Read
    /// from the raw lines rather than the masked ones, because the claim lives in a comment.
    /// </summary>
    private static bool IsJustified(string[] lines, int line)
    {
        var first = Math.Max(0, line - 1 - JustificationLookbackLines);
        for (var index = first; index < line && index < lines.Length; index++)
        {
            var marker = lines[index].IndexOf(JustificationMarker, StringComparison.Ordinal);
            if (marker < 0)
                continue;

            if (!string.IsNullOrWhiteSpace(lines[index][(marker + JustificationMarker.Length)..]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Blanks string literals and the trailing line comment while preserving every offset.
    /// </summary>
    /// <remarks>
    /// Splitting on <c>//</c> alone had two defects. A <c>Task.Delay(...)</c> written INSIDE a string
    /// was counted as a wait - <c>SubAgentEagerConversationPinTests</c> carries one in an assertion
    /// message that exists precisely to tell the reader not to add a delay, and it was scored as
    /// debt. And a <c>//</c> inside a string (any URL) truncated the line early, hiding real code
    /// after it. Verbatim and raw string literals spanning lines are not handled; this is a
    /// line-based scanner and they have not appeared in a finite-wait line.
    /// </remarks>
    private static string MaskStringsAndComments(string line)
    {
        var masked = line.ToCharArray();
        var inString = false;
        var inChar = false;

        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];

            if (!inString && !inChar && current == '/' && index + 1 < line.Length && line[index + 1] == '/')
            {
                for (var rest = index; rest < line.Length; rest++)
                    masked[rest] = ' ';
                break;
            }

            if (current == '\\' && (inString || inChar) && index + 1 < line.Length)
            {
                masked[index] = ' ';
                masked[index + 1] = ' ';
                index++;
                continue;
            }

            if (current == '"' && !inChar)
            {
                inString = !inString;
                continue;
            }

            if (current == '\'' && !inString)
            {
                inChar = !inChar;
                continue;
            }

            if (inString || inChar)
                masked[index] = ' ';
        }

        return new string(masked);
    }

    [GeneratedRegex(@"\b(?:Task\.Delay|Thread\.Sleep)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CreateFiniteWaitRegex();
}