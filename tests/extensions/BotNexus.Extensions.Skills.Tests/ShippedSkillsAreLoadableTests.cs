using System.IO.Abstractions;
using System.Text.RegularExpressions;
using BotNexus.Extensions.Skills;
using Shouldly;

namespace BotNexus.Extensions.Skills.Tests;

/// <summary>
/// Every SKILL.md this repository ships actually loads, and its installer puts it where
/// discovery will find it.
///
/// <remarks>
/// A skill is prose with a frontmatter contract: nothing is compiled and nothing fails loudly.
/// A malformed one is SKIPPED by <see cref="SkillDiscovery"/> with a log line nobody reads, and
/// the agent simply never gains the capability - indistinguishable from the skill working badly.
/// <para>
/// The sharpest edge is that discovery requires the skill's DIRECTORY name to equal its
/// frontmatter name. The install script chooses the directory and the skill author chooses the
/// name, and they are in different files - so they can drift, and when they do the operator
/// installs something that is silently never loaded.
/// </para>
/// </remarks>
/// </summary>
public sealed class ShippedSkillsAreLoadableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    private sealed record Shipped(string Directory, string SkillMdPath, string DeclaredName);

    /// <summary>Every <c>docs/*-skill/SKILL.md</c>, with the name its frontmatter declares.</summary>
    private static IReadOnlyList<Shipped> FindShipped()
    {
        var results = new List<Shipped>();
        foreach (var directory in Directory.GetDirectories(Path.Combine(RepoRoot, "docs"), "*-skill"))
        {
            var skillMd = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(skillMd))
                continue;

            var match = Regex.Match(
                File.ReadAllText(skillMd),
                @"^name:\s*(?<name>[^\r\n]+)$",
                RegexOptions.Multiline);

            match.Success.ShouldBeTrue($"{skillMd} has no 'name:' in its frontmatter");
            results.Add(new Shipped(directory, skillMd, match.Groups["name"].Value.Trim().Trim('"')));
        }

        return results;
    }

    [Fact]
    public void Every_shipped_skill_is_discovered_rather_than_silently_skipped()
    {
        var shipped = FindShipped();
        shipped.ShouldNotBeEmpty("this test is vacuous if it finds no skills to check");

        // Staged the way an installer lays them out: one directory per skill, named for the
        // skill. Discovery rejects any other arrangement, which is the point of the next test.
        var staged = Path.Combine(Path.GetTempPath(), "botnexus-shipped-skills", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var skill in shipped)
            {
                var destination = Path.Combine(staged, skill.DeclaredName);
                Directory.CreateDirectory(destination);
                File.Copy(skill.SkillMdPath, Path.Combine(destination, "SKILL.md"));
            }

            var discovered = SkillDiscovery.Discover(
                staged, agentSkillsDir: null, workspaceSkillsDir: null, new FileSystem());

            discovered.Select(s => s.Name).OrderBy(n => n)
                .ShouldBe(shipped.Select(s => s.DeclaredName).OrderBy(n => n));
        }
        finally
        {
            if (Directory.Exists(staged))
                Directory.Delete(staged, recursive: true);
        }
    }

    [Fact]
    public void Each_installer_writes_the_skill_to_a_directory_named_after_it()
    {
        // THE test in this file. SkillDiscovery.TryValidate rejects a skill whose frontmatter
        // name differs from its containing directory - so if an install script's destination and
        // the skill's declared name drift apart, the operator installs a skill that is skipped
        // with a log line and never loads. Nothing else in the build notices; the two values
        // live in different files and neither is compiled.
        foreach (var skill in FindShipped())
        {
            var installer = Directory
                .GetFiles(Path.Combine(RepoRoot, "scripts"), "install-*-skill.sh")
                .Select(File.ReadAllText)
                .FirstOrDefault(text => text.Contains(Path.GetFileName(skill.Directory), StringComparison.Ordinal));

            installer.ShouldNotBeNull($"no install script references docs/{Path.GetFileName(skill.Directory)}");
            installer.ShouldContain(
                $"/skills/{skill.DeclaredName}",
                Case.Sensitive,
                $"the installer must write '{skill.DeclaredName}' to a directory of that name, or discovery skips it");
        }
    }

    [Fact]
    public void A_skills_description_says_when_to_use_it_not_just_what_it_is()
    {
        // The description is the only thing a model sees when deciding whether to load a skill.
        // One that describes the contents rather than the trigger is a skill that never fires.
        foreach (var skill in FindShipped())
        {
            var content = File.ReadAllText(skill.SkillMdPath);
            var description = Regex.Match(
                content, @"^description:\s*(?<d>[^\r\n]+)$", RegexOptions.Multiline);

            description.Success.ShouldBeTrue($"{skill.DeclaredName} has no description");
            description.Groups["d"].Value.ShouldContain(
                "Use",
                Case.Insensitive,
                $"'{skill.DeclaredName}' never states when to use it, so nothing will choose it");
        }
    }
}
