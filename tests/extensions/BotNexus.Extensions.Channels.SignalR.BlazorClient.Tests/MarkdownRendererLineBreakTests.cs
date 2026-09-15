using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// <c>renderMarkdown</c> keeps a chat message's newlines as line breaks, but the guide passes
/// <c>{ breaks: false }</c> so hard-wrapped documentation reflows to the window. Rendering a
/// guide page with breaks on pins every paragraph to its ~90-column source width however wide
/// the window is, which is what the portal page looked like before this option existed.
///
/// Runs the real <c>markdown.js</c> and the real bundled <c>marked.min.js</c> under Node — the
/// stubbed marked in <see cref="MarkdownRendererFailClosedTests"/> ignores options, so it could
/// not tell these two cases apart.
/// </summary>
public sealed class MarkdownRendererLineBreakTests
{
    private static readonly string JsDir = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot", "js"));

    private const string HardWrapped = "The portal is the web interface\nthe gateway serves.";

    [Fact]
    public void Chat_default_keeps_a_newline_as_a_line_break()
    {
        var output = Render(HardWrapped, optionsJs: null);

        Assert.Contains("<br>", output);
    }

    [Fact]
    public void Breaks_false_lets_a_hard_wrapped_paragraph_reflow()
    {
        var output = Render(HardWrapped, optionsJs: "{ breaks: false }");

        Assert.DoesNotContain("<br>", output);
        Assert.Contains("<p>The portal is the web interface\nthe gateway serves.</p>", output);
    }

    [Fact]
    public void Breaks_false_leaves_code_block_newlines_alone()
    {
        var output = Render("```\nline one\nline two\n```", optionsJs: "{ breaks: false }");

        Assert.Contains("line one\nline two", output);
    }

    private static string Render(string input, string? optionsJs)
    {
        var markdownJs = Path.Combine(JsDir, "markdown.js");
        var markedJs = Path.Combine(JsDir, "marked.min.js");
        Assert.True(File.Exists(markdownJs), $"markdown.js not found at {markdownJs}");
        Assert.True(File.Exists(markedJs), $"marked.min.js not found at {markedJs}");

        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-md-breaks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var driverPath = Path.Combine(tempDir, "driver.js");
            var args = System.Text.Json.JsonSerializer.Serialize(input) + (optionsJs is null ? "" : ", " + optionsJs);
            File.WriteAllText(driverPath, $$"""
                globalThis.window = globalThis;
                globalThis.navigator = globalThis.navigator || {};
                globalThis.document = { createElement: function () { return { style: {}, setAttribute: function () { }, addEventListener: function () { } }; } };
                (0, eval)(require('fs').readFileSync({{System.Text.Json.JsonSerializer.Serialize(markedJs)}}, 'utf8'));
                if (typeof marked === 'undefined') { process.stderr.write('marked did not load; the renderer would fall back to escaped text'); process.exit(2); }
                globalThis.DOMPurify = { sanitize: function (html) { return html; } };
                require({{System.Text.Json.JsonSerializer.Serialize(markdownJs)}});
                process.stdout.write(String(window.BotNexus.renderMarkdown({{args}})));
                """);

            var psi = new ProcessStartInfo("node", "\"" + driverPath + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            Assert.NotNull(process);

            var stdout = process!.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"node driver failed (exit {process.ExitCode}). stderr: {stderr}");

            return stdout;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }
}
